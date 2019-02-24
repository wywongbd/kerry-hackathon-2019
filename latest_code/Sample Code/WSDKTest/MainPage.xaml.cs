using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using DJI.WindowsSDK;
using Windows.UI.Popups;
using Windows.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.Common;
using ZXing.Multi.QrCode;
using Windows.Graphics.Imaging;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using DJIDemo.AIModel;
using Windows.Storage.Streams;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using DJIDemo.Controls;
using WSDKTest.Controls;
using System.Linq;
using OpenCvSharp;

namespace WSDKTest
{
    public sealed partial class MainPage : Page
    {
        public Dictionary<string, Dictionary<string, float>> boxToLocations = new Dictionary<string, Dictionary<string, float>>();
        public Dictionary<string, float> boxToMinDist = new Dictionary<string, float>();
        public Dictionary<string, string> boxToClosestLocation = new Dictionary<string, string>();
        public ISet<string> seenLocations = new HashSet<string>();
        private IDictionary<DecodeHintType, object> decodeHints = new Dictionary<DecodeHintType, object>();
        private DJIVideoParser.Parser videoParser;
        public WriteableBitmap VideoSource;

        //Worker task (thread) for reading barcode
        //As reading barcode is computationally expensive
        private Task readerWorker = null;
        private Task controlWorker = null;

        private object bufLock = new object();
        //these properties are guarded by bufLock
        private int width, height;
        private byte[] decodedDataBuf;

        // ML model object
        private ProcessWithONNX processWithONNX = null;
        private Task runProcessTask = null;

        // New Task for Showing Video
        private Task showVideoTask = null;
        private Task myWorker = null;


        public MainPage()
        {
            this.InitializeComponent();
            //Listen for registration success
            DJISDKManager.Instance.SDKRegistrationStateChanged += async (state, result) =>
            {
                if (state != SDKRegistrationState.Succeeded)
                {
                    var md = new MessageDialog(result.ToString());
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async ()=> await md.ShowAsync());
                    return;
                }
                //wait till initialization finish
                //use a large enough time and hope for the best
                await Task.Delay(1000);
                videoParser = new DJIVideoParser.Parser();
                videoParser.Initialize();
                videoParser.SetVideoDataCallack(0, 0, ReceiveDecodedData);
                DJISDKManager.Instance.VideoFeeder.GetPrimaryVideoFeed(0).VideoDataUpdated += OnVideoPush;

                await DJISDKManager.Instance.ComponentManager.GetFlightAssistantHandler(0, 0).SetObstacleAvoidanceEnabledAsync(new BoolMsg() { value = false });


                await Task.Delay(5000);
                GimbalResetCommandMsg resetMsg = new GimbalResetCommandMsg() { value = GimbalResetCommand.UNKNOWN };

                await DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0, 0).ResetGimbalAsync(resetMsg);
            };
            DJISDKManager.Instance.RegisterApp("b4e808daf5598cc74726bb39");
            processWithONNX = new ProcessWithONNX();
            qrCodeReader = new QRCodeMultiReader();
            decodeHints.Add(DecodeHintType.TRY_HARDER, true);
        }

        void OnVideoPush(VideoFeed sender, [ReadOnlyArray] ref byte[] bytes)
        {
            videoParser.PushVideoData(0, 0, bytes, bytes.Length);
        }

        private QRCodeMultiReader qrCodeReader;

        public Result[] DecodeQRCode(SoftwareBitmap bitmap)
        {
            var reader = qrCodeReader;
            HybridBinarizer binarizer;
            var source = new SoftwareBitmapLuminanceSource(bitmap);
            binarizer = new HybridBinarizer(source);
            return reader.decodeMultiple(new BinaryBitmap(binarizer));
        }

        // save matched pairs to a csv files
        public async void WriteToCsv(Dictionary<string, string> data)
        {
            if (data == null)
            {
                return;
            }
            if (data.Count == 0)
            {
                return;
            }
            String csv = String.Join(
                Environment.NewLine,
                data.Select(d => d.Key + "," + d.Value)
            );

            string fileName = DateTime.Now.ToString("MM-dd-yyy-h-mm-ss") + ".csv";
            Windows.Storage.StorageFile newFile = await Windows.Storage.DownloadsFolder.CreateFileAsync(fileName);
            await Windows.Storage.FileIO.WriteTextAsync(newFile, csv.ToString());
        }

        private Dictionary<string, string> GetResultPairs()
        {

            var locationToBox = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> entry in boxToClosestLocation)
            {
                if (!locationToBox.ContainsKey(entry.Value))
                {
                    locationToBox[entry.Value] = entry.Key;
                }
            }

            Dictionary<string, string> pairs = new Dictionary<string, string>();
            foreach(var loc in seenLocations)
            {
                if (locationToBox.ContainsKey(loc))
                {
                    pairs[loc] = locationToBox[loc];
                } else
                {
                    pairs[loc] = "";
                }
            }
            return pairs;
        }

        private string PrintPairDictionary()
        {
            var pairing = "";
            foreach (KeyValuePair<string, string> entry in boxToClosestLocation)
            {
                pairing += entry.Value + "," + entry.Key + "\n";
            }
            return pairing;
        }

        private float ManhattanDistance((float, float) p1, (float, float) p2)
        {
            return Math.Abs(p1.Item1 - p2.Item1) + Math.Abs(p1.Item2 - p2.Item2);
        }

        private float L2Distance((float, float) p1, (float, float) p2)
        {
            var dx = p1.Item1 - p2.Item1;
            var dy = p1.Item2 - p2.Item2;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        private float AverageLengthInOneQRCode(Result r)
        {
            var points = r.ResultPoints;
            float l1 = L2Distance((points[0].X, points[0].Y), (points[1].X, points[1].Y));
            float l2 = L2Distance((points[1].X, points[1].Y), (points[2].X, points[2].Y));
            float l3 = L2Distance((points[0].X, points[0].Y), (points[2].X, points[2].Y));
            return (l1 + l2 + l3) / 3;
        }

        private float QRCodeAverageLength(Result[] result_lis)
        {
            float average_len = 0;
            foreach (var r in result_lis)
            {
                average_len += AverageLengthInOneQRCode(r);
            }
            return average_len/result_lis.Length;
        }

        private (float, float) FindCentroid(Result r)
        {
            float x = 0;
            float y = 0;
            foreach (var p in r.ResultPoints)
            {
                x += p.X;
                y += p.Y;
            }
            x /= r.ResultPoints.Length;
            y /= r.ResultPoints.Length;
            return (x, y);
        }

        // detect green lines from image
        public List<double> getGreenLines(Mat img, int threshold, bool takeTopCluster)
        {
            var lowerBound = new OpenCvSharp.Scalar(40, 25, 25);
            var upperBound = new OpenCvSharp.Scalar(80, 255, 255);
            var hsv = img.CvtColor(ColorConversionCodes.BGR2HSV);
            var mask = hsv.InRange(lowerBound, upperBound);
            var invertMask = (255 - mask).ToMat();
            var canny = invertMask.Canny(50, 150, 3);
            var lines = Cv2.HoughLines(canny, 1, Math.PI / 180, 200);

            double max_c = int.MinValue;
            double min_c = int.MaxValue;
            double mean_rho = 0;
            double mean_theta = 0;

            for(int i = 0; i < lines.Length; i++)
            {
                var rho = lines[i].Rho;
                var theta = lines[i].Theta;
                var c = rho / Math.Sin(theta);
                max_c = Math.Max(max_c, c);
                min_c = Math.Min(min_c, c);
                mean_rho += rho;
                mean_theta += theta;
            }

            if (max_c - min_c > threshold)
            {
                mean_rho = 0;
                mean_theta = 0;
                var mid_c = (max_c + min_c) / 2;
                for (int i = 0; i < lines.Length; i++)
                {
                    var rho = lines[i].Rho;
                    var theta = lines[i].Theta;
                    var c = rho / Math.Sin(theta);

                    if(c > mid_c && takeTopCluster)
                    {
                        mean_rho += rho;
                        mean_theta += theta;
                    }
                    else if(c < mid_c && !takeTopCluster)
                    {
                        mean_rho += rho;
                        mean_theta += theta;
                    }
                }

            }
            mean_rho = mean_rho / lines.Length;
            mean_theta = mean_theta / lines.Length;

            return new List<double>(new double[] { mean_rho, mean_theta });
        }

        private async Task<double> GetAltitude()
        {
            var altitude_result = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).GetAltitudeAsync();
            if (altitude_result.value == null)
            {
                return 0;
            }
            else
            {
                return altitude_result.value.Value.value;
            }
        }

        private async Task<double> GetAttitude()
        {
            var attitude_result = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).GetAttitudeAsync();
            if (attitude_result.value == null)
            {
                return 0;
            }
            else
            {
                return attitude_result.value.Value.yaw;
            }
        }

        private async Task MoveDroneToRight()
        {
            var counter = 0;
            start_tracking_end = true;
            await Task.Delay(100);
            while (!is_end && counter < 5 * 7)
            {
                MoveRight(0.5f);
                await Task.Delay(1500);
                Stop();
                await Task.Delay(2000);
                counter++;

                if (qr_len < 60 && qr_len > 45)
                {
                    continue;
                }
                else if (qr_len >= 60)
                {
                    MoveBackward(0.3f);
                    await Task.Delay(500);
                }
                else
                {
                    MoveForward(0.3f);
                    await Task.Delay(500);
                }
            }
            Stop();
        }

        private async Task MoveDroneToLeft()
        {
            var counter = 0;
            start_tracking_end = true;
            await Task.Delay(100);
            while (!is_end && counter < 5 * 7)
            {
                MoveLeft(0.5f);
                await Task.Delay(1500);
                Stop();
                await Task.Delay(2000);
                counter++;

                if (qr_len < 60 && qr_len > 45)
                {
                    continue;
                }
                else if (qr_len >= 60)
                {
                    MoveBackward(0.3f);
                    await Task.Delay(500);
                }
                else
                {
                    MoveForward(0.3f);
                    await Task.Delay(500);
                }
            }
            Stop();
        }
        
        private async Task MoveDroneToLeftLower()
        {
            var counter = 0;
            start_tracking_end = true;
            await Task.Delay(100);
            while (!is_end && counter < 5 * 7)
            {
                MoveLeft(0.5f);
                await Task.Delay(1500);
                Stop();
                await Task.Delay(2000);
                counter++;

                var inner_counter = 0;
                while (!(qr_len < 50 && qr_len > 45) && inner_counter < 6)
                {
                    if (qr_len >= 50)
                    {
                        MoveBackward(0.3f);
                        await Task.Delay(50);
                    }
                    else
                    {
                        MoveForward(0.3f);
                        await Task.Delay(50);
                    }
                    inner_counter++;
                }

                //if (qr_len < 50 && qr_len > 45)
                //{
                //    continue;
                //}
                //else if (qr_len >= 50)
                //{
                //    MoveBackward(0.3f);
                //    await Task.Delay(500);
                //}
                //else
                //{
                //    MoveForward(0.3f);
                //    await Task.Delay(500);
                //}
            }
            Stop();
        }


        private async Task MoveDroneToRightLower()
        {
            var counter = 0;
            start_tracking_end = true;
            await Task.Delay(100);
            while (!is_end && counter < 5 * 7)
            {
                MoveRight(0.5f);
                await Task.Delay(1500);
                Stop();
                await Task.Delay(2000);
                counter++;

                var inner_counter = 0;
                while(!(qr_len < 50 && qr_len > 45) && inner_counter < 6)
                {
                    if (qr_len >= 50)
                    {
                        MoveBackward(0.3f);
                        await Task.Delay(50);
                    }
                    else
                    {
                        MoveForward(0.3f);
                        await Task.Delay(50);
                    }
                    inner_counter++;
                }
                //if (qr_len < 50 && qr_len > 45)
                //{
                //    continue;
                //}
                //else if (qr_len >= 50)
                //{
                //    MoveBackward(0.3f);
                //    await Task.Delay(500);
                //}
                //else
                //{
                //    MoveForward(0.3f);
                //    await Task.Delay(500);
                //}
            }
            Stop();
        }

        int turn_ms = 14200;

        private float qr_len = 0;
        private bool is_end = false;
        private bool start_tracking_end = false;
        void createControlWorker()
        {
            //create worker thread for reading barcode
            controlWorker = new Task(async () =>
            {
                TakeOff();
                //await Task.Delay(7000);

                // successfully take off
                var altitude = await GetAltitude();
                while (altitude < 1.1)
                {
                    await Task.Delay(100);
                    altitude = await GetAltitude();
                }
                await Task.Delay(500);

                // move forward a bit until ...
                var counter = 0;
                while (true)
                {
                    if ((qr_len < 60 && qr_len > 45) || counter >= 100)
                    {
                        break;
                    }
                    else if (qr_len >= 60)
                    {
                        MoveBackward(0.2f);
                        await Task.Delay(20);
                    }
                    else
                    {
                        MoveForward(0.2f);
                        await Task.Delay(20);
                    }
                    counter++;
                }
                Stop();

                // move to right level 3
                await MoveDroneToRight();

                // move down to level 2
                start_tracking_end = false;
                is_end = false;
                MoveDown();
                altitude = await GetAltitude();
                while (altitude > 0.8)
                {
                    await Task.Delay(20);
                    altitude = await GetAltitude();
                }
                Stop();

                // move to left level 2
                await MoveDroneToLeft();

                // move down to level 1
                start_tracking_end = false;
                is_end = false;
                MoveDown();
                altitude = await GetAltitude();
                while (altitude >= 0.5)
                {
                    await Task.Delay(20);
                    altitude = await GetAltitude();
                }
                await Task.Delay(500);
                Stop();


                // move backward a bit until?
                counter = 0;
                while (true)
                {
                    if ((qr_len < 50 && qr_len > 45) || counter >= 100)
                    {
                        break;
                    }
                    else if (qr_len >= 50)
                    {
                        MoveBackward(0.2f);
                        await Task.Delay(50);
                    }
                    else
                    {
                        MoveForward(0.2f);
                        await Task.Delay(50);
                    }
                    counter++;
                }
                Stop();

                // move to right level 1
                await MoveDroneToRight();


                // rotate 180 degree
                TurnRight();
                await Task.Delay(turn_ms);
                //var yaw = await GetAttitude();
                //while (!(yaw < 10 && yaw > 2))
                //{
                //    await Task.Delay(10);
                //    yaw = await GetAttitude();
                //}
                Stop();
                await Task.Delay(100);

                // move forward a bit until ...
                qr_len = 0;
                counter = 0;
                while (true)
                {
                    if ((qr_len < 60 && qr_len > 45) || counter >= 130)
                    {
                        break;
                    }
                    else if (qr_len >= 60)
                    {
                        MoveBackward(0.2f);
                        await Task.Delay(20);
                    }
                    else
                    {
                        MoveForward(0.2f);
                        await Task.Delay(20);
                    }
                    counter++;
                }
                Stop();

                // move to right level 1
                await MoveDroneToRight();

                // move up to level 2
                start_tracking_end = false;
                is_end = false;
                MoveUp();
                altitude = await GetAltitude();
                while (altitude <= 0.5)
                {
                    await Task.Delay(20);
                    altitude = await GetAltitude();
                }
                await Task.Delay(500);
                Stop();

                // move to left level 2
                await MoveDroneToLeft();

                // move up to level 3
                start_tracking_end = false;
                is_end = false;
                MoveUp();
                altitude = await GetAltitude();
                while (altitude <= 1.1)
                {
                    await Task.Delay(20);
                    altitude = await GetAltitude();
                }
                await Task.Delay(500);
                Stop();

                // move to right level 3
                await MoveDroneToRight();


                WriteToCsv(GetResultPairs());

                Landing();
                await Task.Delay(15000);
                altitude = await GetAltitude();
                while (altitude >= 0.1)
                {
                    CancelLanding();
                    MoveUp();
                    await Task.Delay(800);
                    Landing();
                    await Task.Delay(15000);
                    altitude = await GetAltitude();
                }

            });
        }

        void createWorker()
        {
            //create worker thread for reading barcode
            readerWorker = new Task(async () =>
            {
                //use stopwatch to time the execution, and execute the reading process repeatedly
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var reader = new QRCodeMultiReader();               
                SoftwareBitmap bitmap;
                SoftwareBitmap sbp = null;
                HybridBinarizer binarizer;

                // control loop
                while (true)
                {
                    watch.Restart();

                    // for logging the information of this loop
                    string loop_info = "";

                    // display frame
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        try
                        {
                            //dispatch to UI thread to do UI update (image)
                            //WriteableBitmap is exclusive to UI thread
                            if (VideoSource == null || VideoSource.PixelWidth != width || VideoSource.PixelHeight != height)
                            {
                                VideoSource = new WriteableBitmap(width, height);
                                fpvImage.Source = VideoSource;
                                //UpdateTextbox(width + ", " + height);
                            }
                            lock (bufLock)
                            {
                                //copy buffer to the bitmap and draw the region we will read on to notify the users
                                decodedDataBuf.AsBuffer().CopyTo(VideoSource.PixelBuffer);
                            }
                            //Invalidate cache and trigger redraw
                            VideoSource.Invalidate();
                        }
                        catch (Exception)
                        {
                        }
                    });

                    // search for qr code
                    try
                    {
                        lock(bufLock)
                        {

                            var buffer = WindowsRuntimeBufferExtensions.AsBuffer(decodedDataBuf, 0, decodedDataBuf.Length);
                            bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer,
                                    BitmapPixelFormat.Bgra8, (int)width, (int)height, BitmapAlphaMode.Premultiplied);
                            
                            //bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height);
                            //bitmap.CopyFromBuffer(decodedDataBuf.AsBuffer());
                        }
                        
                        var source = new SoftwareBitmapLuminanceSource(bitmap);
                        binarizer = new HybridBinarizer(source);
                        //var results = reader.decodeMultiple(new BinaryBitmap(binarizer));
                        var results = reader.decodeMultiple(new BinaryBitmap(binarizer), decodeHints);

                        // when the qr code detection result is not empty.
                        if (results != null && results.Length > 0)
                        {
                            var average_len = QRCodeAverageLength(results);
                            qr_len = average_len;

                            if (start_tracking_end)
                            {
                                is_end = ProcessQRCode.DetectEnd(results);
                            }

                            // cache locations and boxes
                            List<(string, (float, float))> location_list = new List<(string, (float, float))>();
                            List<(string, (float, float))> box_list = new List<(string, (float, float))>();

                            // distinguish location and non location result.
                            foreach (var result in results)
                            {
                                // cache for later computation
                                if (ProcessQRCode.IsLocation(result.Text))
                                {
                                    location_list.Add((result.Text, FindCentroid(result)));
                                }
                                else if (ProcessQRCode.IsBox(result.Text))
                                {
                                    box_list.Add((result.Text, FindCentroid(result)));
                                }
                            }

                            //loop_info += "box: \n";
                            //foreach (var box in box_list)
                            //{
                            //    loop_info += box.Item1 + "\n";
                            //}

                            //loop_info += "loc: \n";
                            //foreach (var loc in location_list)
                            //{
                            //    loop_info += loc.Item1 + "\n";
                            //}

                            // make sure all locations are saved somewhere
                            foreach (var loc in location_list)
                            {
                                seenLocations.Add(loc.Item1);
                            }

                            if (location_list.Count > 0)
                            {

                                //List<(string, string, float)> box_loc_dist_list = new List<(string, string, float)>();
                                foreach (var box in box_list)
                                {
                                    // box coordinate
                                    (float, float) box_p = box.Item2;

                                    // locations that are below the box coordinate
                                    var valid_locations = new List<(string, (float, float))>();
                                    foreach (var loc in location_list)
                                    {
                                        if (loc.Item2.Item2 > box.Item2.Item2) {
                                            valid_locations.Add(loc);
                                        }
                                    }
                                    

                                    foreach (var loc in valid_locations)
                                    {
                                        (float, float) loc_p = loc.Item2;
                                        var dist = ManhattanDistance(box_p, loc_p);
                                        if (!boxToLocations.ContainsKey(box.Item1))
                                        {
                                            boxToLocations[box.Item1] = new Dictionary<string, float>();
                                            boxToMinDist[box.Item1] = dist;
                                            boxToClosestLocation[box.Item1] = loc.Item1;
                                        }

                                        // update box to location distance
                                        if (!boxToLocations[box.Item1].ContainsKey(loc.Item1))
                                        {
                                            boxToLocations[box.Item1][loc.Item1] = dist;
                                        } else
                                        {
                                            var temp_dist = boxToLocations[box.Item1][loc.Item1];
                                            if (dist < temp_dist)
                                            {
                                                boxToLocations[box.Item1][loc.Item1] = dist;
                                            }
                                        }

                                        // when the box to location distance is the shortest
                                        if (dist < boxToMinDist[box.Item1])
                                        {
                                            boxToMinDist[box.Item1] = dist;
                                            boxToClosestLocation[box.Item1] = loc.Item1;
                                        }
                                    }
                                }
                            }


                            //// if there are any non location qr code, use distance to match which location associated with it
                            //await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                            //{
                            //    foreach (var result in results)
                            //    {
                            //        if (!readed.Contains(result.Text))
                            //        {
                            //            readed.Add(result.Text);
                            //            //Textbox.Text += result.Text + "\n";
                            //        }
                            //    }
                            //});
                        }

                        loop_info += "pairing: \n";
                        loop_info += PrintPairDictionary();
                    }
                    catch (Exception e)
                    {
                        loop_info += e.ToString();
                        ////the size maybe incorrect due to unknown reason
                        //await Task.Delay(10);
                        //continue;
                    }

                    //// object detection
                    //try
                    //{
                    //    //UpdateTextbox("In ProcessSoftwareBitmap \n");

                    //    if (bitmap.PixelHeight != bitmap.PixelWidth)
                    //    {
                    //        int destWidthAndHeight = 416;
                    //        using (var resourceCreator = CanvasDevice.GetSharedDevice())
                    //        using (var canvasBitmap = CanvasBitmap.CreateFromSoftwareBitmap(resourceCreator, bitmap))
                    //        using (var canvasRenderTarget = new CanvasRenderTarget(resourceCreator, destWidthAndHeight, destWidthAndHeight, canvasBitmap.Dpi))
                    //        using (var drawingSession = canvasRenderTarget.CreateDrawingSession())
                    //        using (var scaleEffect = new ScaleEffect())
                    //        {
                    //            scaleEffect.Source = canvasBitmap;
                    //            scaleEffect.Scale = new System.Numerics.Vector2((float)destWidthAndHeight / (float)bitmap.PixelWidth, (float)destWidthAndHeight / (float)bitmap.PixelHeight);
                    //            drawingSession.DrawImage(scaleEffect);
                    //            drawingSession.Flush();

                    //            sbp = SoftwareBitmap.CreateCopyFromBuffer(canvasRenderTarget.GetPixelBytes().AsBuffer(), BitmapPixelFormat.Bgra8, destWidthAndHeight, destWidthAndHeight, BitmapAlphaMode.Premultiplied);

                    //            var tempString = await processWithONNX.ProcessSoftwareBitmap(sbp, bitmap, this);
                    //            UpdateTextbox(tempString);

                    //        }
                    //    }
                    //    else
                    //    {
                    //        var tempString = await processWithONNX.ProcessSoftwareBitmap(bitmap, bitmap, this);
                    //        UpdateTextbox(tempString);
                    //    }

                    //    UpdateTextbox("finished object detection.\n");
                    //}
                    //catch (Exception e)
                    //{
                    //    UpdateTextbox(e.ToString()+"\n");
                    //    //string ss = e.Message;
                    //    //await Task.Delay(1000);
                    //}
                    //finally
                    //{
                    //    bitmap.Dispose();
                    //}

                    loop_info += await GetDroneInfoAsync();
                    loop_info += "width, height: " + width + ", " + height + "\n";

                    // finish processing
                    watch.Stop();
                    int elapsed = (int)watch.ElapsedMilliseconds;
                    loop_info += "Time elapsed: " + elapsed + "\n";

                    loop_info += "qr_len: " + qr_len+"\n";
                    loop_info += "is_end: " + is_end + "\n";
                    RewriteTextbox(loop_info);
                    //run at max 5Hz
                    await Task.Delay(Math.Max(0, 100 - elapsed));
                }
            });
        }

        private async Task<string> GetDroneInfoAsync()
        {
            var loop_info = "";
            var altitude_result = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).GetAltitudeAsync();
            if (altitude_result.value == null)
            {
                loop_info += "height: " + "null" + "\n";
            }
            else
            {
                loop_info += "height: " + altitude_result.value.Value.value + "\n";
            }

            var attitude_result = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).GetAttitudeAsync();
            if (attitude_result.value == null)
            {
                loop_info += "attitude: " + "null" + "\n";
            }
            else
            {
                loop_info += "roll: " + attitude_result.value.Value.roll + "\n";
                loop_info += "pitch: " + attitude_result.value.Value.pitch + "\n";
                loop_info += "yaw: " + attitude_result.value.Value.yaw + "\n";
            }

            var flying = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).GetIsFlyingAsync();
            if (flying.value == null)
            {
                loop_info += "flying: " + "null" + "\n";
            }
            else
            {
                loop_info += "flying: " + flying.value.Value.value + "\n";
            }

            //var resolu = await DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).GetVideoResolutionAndFrameRateAsync();
            //if (resolu.value == null)
            //{
            //    loop_info += "resolution: " + "null" + "\n";
            //}
            //else
            //{
            //    loop_info += "resolution: " + resolu.value.Value.resolution.ToString() +", "+ resolu.value.Value.frameRate.ToString()+ "\n";
            //    if (resolu.value.Value.resolution != VideoResolution.RESOLUTION_3840x2160)
            //    {
            //        var resolu_frame = new VideoResolutionAndFrameRate();
            //        resolu_frame.resolution = VideoResolution.RESOLUTION_3840x2160;
            //        resolu_frame.frameRate = VideoFrameRate.RATE_30FPS;
            //        await DJISDKManager.Instance.ComponentManager.GetCameraHandler(0, 0).SetVideoResolutionAndFrameRateAsync(resolu_frame);
            //    }
            //}
            return loop_info;
        }

        void createMyWorker()
        {
            //create worker thread for reading barcode
            myWorker = new Task(async () =>
            {
                //use stopwatch to time the execution, and execute the reading process repeatedly
                var watch = System.Diagnostics.Stopwatch.StartNew();
                watch.Stop();

                while (true)
                {
                    watch.Start();
                    lock (bufLock)
                    {
                        ShowVideo(decodedDataBuf, width, height);
                    }
                    watch.Stop();
                    int elapsed = (int)watch.ElapsedMilliseconds;
                    //run at max 5Hz
                    await Task.Delay(Math.Max(0, 200 - elapsed));
                }
            });
        }


        public async void UpdateTextbox(string str) {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                Textbox.Text += str;
            });

        }


        public async void RewriteTextbox(string str)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                Textbox.Text = str;
            });

        }

        public Task DJIClient_ShowVideo(IBuffer buffer, uint width, uint height) {
            return Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    if (VideoSource == null || VideoSource.PixelWidth != width || VideoSource.PixelHeight != height)
                    {
                        VideoSource = new WriteableBitmap((int)width, (int)height);
                    }

                    buffer.CopyTo(VideoSource.PixelBuffer);

                    fpvImage.Source = VideoSource;
                    VideoSource.Invalidate();
                }
                catch (Exception)
                {
                }
            }).AsTask();
        }

        public void ShowVideo(byte[] data, int witdth, int height)
        {
            DjiClient_FrameArrived(WindowsRuntimeBufferExtensions.AsBuffer(data, 0, data.Length), (uint)witdth, (uint)height);
        }

        // extra copy from microsoft UWPSample
        public async void DjiClient_FrameArrived(IBuffer buffer, uint width, uint height)
        {
            if (runProcessTask == null || runProcessTask.IsCompleted)
            {
                // Do not forget to dispose it! In this sample, we dispose it in ProcessSoftwareBitmap
                try
                {

                    SoftwareBitmap bitmapToProcess = SoftwareBitmap.CreateCopyFromBuffer(buffer,
                            BitmapPixelFormat.Bgra8, (int)width, (int)height, BitmapAlphaMode.Premultiplied);
                    runProcessTask = ProcessSoftwareBitmap(bitmapToProcess);
                }
                catch (Exception e)
                {
                    var dummy = "in DjiClient_Frame_Arrived \n";
                }
            }

            //if (showVideoTask == null || showVideoTask.IsCompleted)
            //{
            //    // Do not forget to dispose it! In this sample, we dispose it in ProcessSoftwareBitmap
            //    try
            //    {

            //        showVideoTask = DJIClient_ShowVideo(buffer, width, height);

            //    }
            //    catch (Exception e)
            //    {
            //        var dummy = "in DjiClient_Frame_Arrived \n";
            //    }
            //}

            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    if (VideoSource == null || VideoSource.PixelWidth != width || VideoSource.PixelHeight != height)
                    {
                        VideoSource = new WriteableBitmap((int)width, (int)height);
                    }

                    buffer.CopyTo(VideoSource.PixelBuffer);

                    fpvImage.Source = VideoSource;
                    VideoSource.Invalidate();
                }
                catch (Exception)
                {
                }
            });
        }

        // extra copy from microsoft UWPSample
        private async Task ProcessSoftwareBitmap(SoftwareBitmap bitmap)
        {
            //try
            //{
            //    //UpdateTextbox("In ProcessSoftwareBitmap \n");

            //    if (bitmap.PixelHeight != bitmap.PixelWidth)
            //    {
            //        int destWidthAndHeight = 416;
            //        using (var resourceCreator = CanvasDevice.GetSharedDevice())
            //        using (var canvasBitmap = CanvasBitmap.CreateFromSoftwareBitmap(resourceCreator, bitmap))
            //        using (var canvasRenderTarget = new CanvasRenderTarget(resourceCreator, destWidthAndHeight, destWidthAndHeight, canvasBitmap.Dpi))
            //        using (var drawingSession = canvasRenderTarget.CreateDrawingSession())
            //        using (var scaleEffect = new ScaleEffect())
            //        {
            //            scaleEffect.Source = canvasBitmap;
            //            scaleEffect.Scale = new System.Numerics.Vector2((float)destWidthAndHeight / (float)bitmap.PixelWidth, (float)destWidthAndHeight / (float)bitmap.PixelHeight);
            //            drawingSession.DrawImage(scaleEffect);
            //            drawingSession.Flush();

            //            var sbp = SoftwareBitmap.CreateCopyFromBuffer(canvasRenderTarget.GetPixelBytes().AsBuffer(), BitmapPixelFormat.Bgra8, destWidthAndHeight, destWidthAndHeight, BitmapAlphaMode.Premultiplied);

                        
            //            var tempString = await processWithONNX.ProcessSoftwareBitmap(sbp, this);
            //            UpdateTextbox(tempString);
            //        }
            //    }
            //    else
            //    {
            //        var tempString = await processWithONNX.ProcessSoftwareBitmap(bitmap, this);
            //        UpdateTextbox(tempString);
            //    }



            //}
            //catch (Exception e)
            //{
            //    string ss = e.Message;
            //    await Task.Delay(1000);
            //}
            //finally
            //{
            //    bitmap.Dispose();
            //}
        }

        void ReceiveDecodedData(byte[] data, int width, int height)
        {
            //basically copied from the sample code
            lock (bufLock)
            {
                //lock when updating decoded buffer, as this is run in async
                //some operation in this function might overlap, so operations involving buffer, width and height must be locked
                if (decodedDataBuf == null)
                {
                    decodedDataBuf = data;
                }
                else
                {
                    if (data.Length != decodedDataBuf.Length)
                    {
                        Array.Resize(ref decodedDataBuf, data.Length);
                    }
                    data.CopyTo(decodedDataBuf.AsBuffer());
                    this.width = width;
                    this.height = height;
                }
            }

            if (readerWorker == null)
            {
                createWorker();
                readerWorker.Start();
            }
        }

        private void Stop_Button_Click(object sender, RoutedEventArgs e)
        {
            var throttle = 0;
            var roll = 0;
            var pitch = 0;
            var yaw = 0;

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }
        
        private async void TakeOff()
        {
            await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).StartTakeoffAsync();
        }


        private async void Landing()
        {
            await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).StartAutoLandingAsync();
        }


        private async void CancelLanding()
        {
            await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).StopAutoLandingAsync();
        }


        private void MoveForward(float mag)
        {
            float throttle = 0;
            float roll = 0;
            float pitch = mag;
            float yaw = 0;

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }


        private void MoveBackward(float mag)
        {
            float throttle = 0;
            float roll = 0;
            float pitch = -mag;
            float yaw = 0;

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }


        private void MoveRight(float mag)
        {
            float throttle = 0;
            float roll = mag;
            float pitch = 0;
            float yaw = 0;

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }

        private void MoveLeft(float mag)
        {
            float throttle = 0;
            float roll = -mag;
            float pitch = 0;
            float yaw = 0;

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }

        private void MoveUp()
        {
            float throttle = 0.2f;
            float roll = 0;
            float pitch = 0;
            float yaw = 0;

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }

        private void MoveDown()
        {
            float throttle = -0.5f;
            float roll = 0;
            float pitch = 0;
            float yaw = 0;

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }

        private async void GimbalUp()
        {
            GimbalAngleRotation rotation = new GimbalAngleRotation()
            {
                mode = GimbalAngleRotationMode.RELATIVE_ANGLE,
                pitch = 45,
                roll = 45,
                yaw = 45,
                pitchIgnored = false,
                yawIgnored = false,
                rollIgnored = false,
                duration = 0.5
            };

            System.Diagnostics.Debug.Write("pitch = 45\n");

            // Defined somewhere else
            var gimbalHandler = DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0, 0);

            //angle
            //var gimbalRotation = new GimbalAngleRotation();
            //gimbalRotation.pitch = 45;
            //gimbalRotation.pitchIgnored = false;
            //gimbalRotation.duration = 5;
            //await gimbalHandler.RotateByAngleAsync(gimbalRotation);

            //Speed
            var gimbalRotation_speed = new GimbalSpeedRotation();
            gimbalRotation_speed.pitch = 10;
            await gimbalHandler.RotateBySpeedAsync(gimbalRotation_speed);
        }

        private async void GimbalDown()
        {
            GimbalAngleRotation rotation = new GimbalAngleRotation()
            {
                mode = GimbalAngleRotationMode.RELATIVE_ANGLE,
                pitch = 45,
                roll = 45,
                yaw = 45,
                pitchIgnored = false,
                yawIgnored = false,
                rollIgnored = false,
                duration = 0.5
            };

            System.Diagnostics.Debug.Write("pitch = 45\n");

            // Defined somewhere else
            var gimbalHandler = DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0, 0);

            //Speed
            var gimbalRotation_speed = new GimbalSpeedRotation();
            gimbalRotation_speed.pitch = -10;
            await gimbalHandler.RotateBySpeedAsync(gimbalRotation_speed);
        }

        private void TurnRight()
        {
            float throttle = 0;
            float roll = 0;
            float pitch = 0;
            float yaw = -0.3f;

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }

        private void Stop()
        {
            float throttle = 0;
            float roll = 0;
            float pitch = 0;
            float yaw = 0;

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }




        private float throttle = 0;
        private float roll = 0;
        private float pitch = 0;
        private float yaw = 0;

        private async void Grid_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.W:
                case Windows.System.VirtualKey.S:
                    {
                        throttle = 0;
                        break;
                    }
                case Windows.System.VirtualKey.A:
                case Windows.System.VirtualKey.D:
                    {
                        yaw = 0;
                        break;
                    }
                case Windows.System.VirtualKey.I:
                case Windows.System.VirtualKey.K:
                    {
                        pitch = 0;
                        break;
                    }
                case Windows.System.VirtualKey.J:
                case Windows.System.VirtualKey.L:
                    {
                        roll = 0;
                        break;
                    }
                case Windows.System.VirtualKey.G:
                    {
                        var res = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).StartTakeoffAsync();
                        break;
                    }
                case Windows.System.VirtualKey.H:
                    {
                        var res = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).StartAutoLandingAsync();
                        WriteToCsv(GetResultPairs());
                        break;
                    }
                case Windows.System.VirtualKey.N:
                    {
                        var res = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).StopAutoLandingAsync();
                        break;
                    }

            }

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch (Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }

        private async void Grid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.W:
                    {
                        throttle += 0.02f;
                        if (throttle > 0.5f)
                            throttle = 0.5f;
                        break;
                    }
                case Windows.System.VirtualKey.S:
                    {
                        throttle -= 0.02f;
                        if (throttle < -0.5f)
                            throttle = -0.5f;
                        break;
                    }
                case Windows.System.VirtualKey.A:
                    {
                        yaw -= 0.05f;
                        if (yaw > 0.5f)
                            yaw = 0.5f;
                        break;
                    }
                case Windows.System.VirtualKey.D:
                    {
                        yaw += 0.05f;
                        if (yaw < -0.5f)
                            yaw = -0.5f;
                        break;
                    }
                case Windows.System.VirtualKey.I:
                    {
                        pitch += 0.05f;
                        if (pitch > 0.5)
                            pitch = 0.5f;
                        break;
                    }
                case Windows.System.VirtualKey.K:
                    {
                        pitch -= 0.05f;
                        if (pitch < -0.5f)
                            pitch = -0.5f;
                        break;
                    }
                case Windows.System.VirtualKey.J:
                    {
                        roll = -0.5f;
                        //roll -= 0.05f;
                        //if (roll < -0.1f)
                        //    roll = -0.1f;
                        break;
                    }
                case Windows.System.VirtualKey.L:
                    {
                        roll = 0.5f;
                        //roll += 0.05f;
                        //if (roll > 0.1)
                        //    roll = 0.1f;
                        break;
                    }
                case Windows.System.VirtualKey.Number0:
                    {
                        GimbalAngleRotation rotation = new GimbalAngleRotation()
                        {
                            mode = GimbalAngleRotationMode.RELATIVE_ANGLE,
                            pitch = 45,
                            roll = 45,
                            yaw = 45,
                            pitchIgnored = false,
                            yawIgnored = false,
                            rollIgnored = false,
                            duration = 0.5
                        };

                        System.Diagnostics.Debug.Write("pitch = 45\n");

                        // Defined somewhere else
                        var gimbalHandler = DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0, 0);

                        //angle
                        //var gimbalRotation = new GimbalAngleRotation();
                        //gimbalRotation.pitch = 45;
                        //gimbalRotation.pitchIgnored = false;
                        //gimbalRotation.duration = 5;
                        //await gimbalHandler.RotateByAngleAsync(gimbalRotation);

                        //Speed
                        var gimbalRotation_speed = new GimbalSpeedRotation();
                        gimbalRotation_speed.pitch = 10;
                        await gimbalHandler.RotateBySpeedAsync(gimbalRotation_speed);

                        //await DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0,0).RotateByAngleAsync(rotation);

                        break;
                    }
                case Windows.System.VirtualKey.P:
                    {
                        GimbalAngleRotation rotation = new GimbalAngleRotation()
                        {
                            mode = GimbalAngleRotationMode.RELATIVE_ANGLE,
                            pitch = 45,
                            roll = 45,
                            yaw = 45,
                            pitchIgnored = false,
                            yawIgnored = false,
                            rollIgnored = false,
                            duration = 0.5
                        };

                        System.Diagnostics.Debug.Write("pitch = 45\n");

                        // Defined somewhere else
                        var gimbalHandler = DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0, 0);

                        //Speed
                        var gimbalRotation_speed = new GimbalSpeedRotation();
                        gimbalRotation_speed.pitch = -10;
                        await gimbalHandler.RotateBySpeedAsync(gimbalRotation_speed);

                        //await DJISDKManager.Instance.ComponentManager.GetGimbalHandler(0,0).RotateByAngleAsync(rotation);

                        break;
                    }
                case Windows.System.VirtualKey.B:
                    {
                        if (controlWorker == null)
                        {
                            createControlWorker();
                            controlWorker.Start();
                        }
                        break;
                    }
            }

            try
            {
                if (DJISDKManager.Instance != null)
                    DJISDKManager.Instance.VirtualRemoteController.UpdateJoystickValue(throttle, yaw, pitch, roll);
            }
            catch(Exception err)
            {
                System.Diagnostics.Debug.WriteLine(err);
            }
        }

        public async void ShowMessagePopup(string message)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    NotifyPopup notifyPopup = new NotifyPopup(message);
                    notifyPopup.Show();
                }
                catch (Exception)
                {

                }
            });
        }

        public Canvas GetCanvas()
        {
            return MLResult;
        }

        public int GetWidth()
        {
            return (int)fpvImage.ActualWidth;
        }

        public int GetHeight()
        {
            return (int)fpvImage.ActualHeight;
        }
    }
}
