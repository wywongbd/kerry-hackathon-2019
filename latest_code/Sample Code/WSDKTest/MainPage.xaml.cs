﻿using System;
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

namespace WSDKTest
{
    public sealed partial class MainPage : Page
    {
        public Dictionary<string, string> matchedPairs = new Dictionary<string, string>();
        private IDictionary<DecodeHintType, object> decodeHints = new Dictionary<DecodeHintType, object>();
        private DJIVideoParser.Parser videoParser;
        public WriteableBitmap VideoSource;

        //Worker task (thread) for reading barcode
        //As reading barcode is computationally expensive
        private Task readerWorker = null;
        public ISet<string> readed = new HashSet<string>();
        public ISet<string> matched = new HashSet<string>();

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
            var tempDict = new Dictionary<string, string>();
            tempDict["abc"] = "efg";
            tempDict["123"] = "456";
            WriteToCsv(tempDict);
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
            String csv = String.Join(
                Environment.NewLine,
                data.Select(d => d.Key + "," + d.Value)
            );

            string fileName = DateTime.Now.ToString("MM-dd-yyy-h-mm-tt") + ".csv";
            Windows.Storage.StorageFile newFile = await Windows.Storage.DownloadsFolder.CreateFileAsync(fileName);
            await Windows.Storage.FileIO.WriteTextAsync(newFile, csv.ToString());
        }

        private float ManhattanDistance((float, float) p1, (float, float) p2)
        {
            return Math.Abs(p1.Item1 - p2.Item1) + Math.Abs(p1.Item2 - p2.Item2);
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
                while (true)
                {
                    watch.Restart();
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
                        var results = reader.decodeMultiple(new BinaryBitmap(binarizer));
                        //var results = reader.decodeMultiple(new BinaryBitmap(binarizer), decodeHints);
                        //if (results != null && results.Length > 0)
                        //{
                        //    // only cache unmatched location and box
                        //    List<(string, (float, float))> location_list = new List<(string, (float, float))>();
                        //    List<(string, (float, float))> box_list = new List<(string, (float, float))>();

                        //    // distinguish location and non location result.
                        //    foreach (var result in results)
                        //    {
                        //        // unmatched location or box
                        //        if (!matched.Contains(result.Text))
                        //        {
                        //            // cache for later computation
                        //            if (ProcessQRCode.IsLocation(result.Text))
                        //            {
                        //                location_list.Add((result.Text, FindCentroid(result)));
                        //            }
                        //            else
                        //            {
                        //                box_list.Add((result.Text, FindCentroid(result)));
                        //            }
                        //        }
                        //    }

                        //    // make sure all location are put into the matchedpairs
                        //    foreach (var loc in location_list)
                        //    {
                        //        // all locations are unmatched
                        //        // which means i can simply set their value to be null.
                        //        // set null for now. will update later
                        //        matchedPairs[loc.Item1] = null;
                        //    }

                        //    if (location_list.Count > 0)
                        //    {
                        //        foreach (var box in box_list)
                        //        {
                        //            var valid_locations = new List<(string, (float, float))>();



                        //            (float, float) box_p = box.Item2;
                        //            string closestLocation = location_list[0].Item1;
                        //            float shortestDistance = ManhattanDistance(box_p, location_list[0].Item2);

                        //            // find the closest location to the given box
                        //            foreach (var loc in location_list)
                        //            {
                        //                (float, float) loc_p = loc.Item2;
                        //                var dis = ManhattanDistance(box_p, loc_p);
                        //                if (dis < shortestDistance)
                        //                {
                        //                    closestLocation = loc.Item1;
                        //                    shortestDistance = dis;
                        //                }
                        //            }
                        //        }
                        //    }


                            // if there are any non location qr code, use distance to match which location associated with it

                            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                            {
                                foreach (var result in results)
                                {
                                    if (!readed.Contains(result.Text))
                                    {
                                        readed.Add(result.Text);
                                        //Textbox.Text += result.Text + "\n";
                                    }
                                }
                            });
                        }
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

                    var altitude_result = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).GetAltitudeAsync();
                    loop_info += "height: " + altitude_result.value.Value.value + "\n";

                    var attitude_result = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).GetAttitudeAsync();
                    if (attitude_result.value == null)
                    {
                        loop_info += "attitude: " + "null" + "\n";
                    } else
                    {
                        loop_info += "roll: " + attitude_result.value.Value.roll + "\n";
                        loop_info += "pitch: " + attitude_result.value.Value.pitch + "\n";
                        loop_info += "yaw: " + attitude_result.value.Value.yaw + "\n";
                    }

                    var flying = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).GetIsFlyingAsync();
                    loop_info += "flying: " + flying.value.Value.value+"\n";


                    // finish processing
                    watch.Stop();
                    int elapsed = (int)watch.ElapsedMilliseconds;
                    loop_info += "Time elapsed: " + elapsed + "\n";
                    RewriteTextbox(loop_info);
                    //run at max 5Hz
                    await Task.Delay(Math.Max(0, 100 - elapsed));
                }
            });
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
                        roll -= 0.05f;
                        if (roll < -0.5f)
                            roll = -0.5f;
                        break;
                    }
                case Windows.System.VirtualKey.L:
                    {
                        roll += 0.05f;
                        if (roll > 0.5)
                            roll = 0.5f;
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
