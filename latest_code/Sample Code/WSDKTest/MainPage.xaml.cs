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
            String csv = String.Join(
                Environment.NewLine,
                data.Select(d => d.Key + "," + d.Value)
            );

            string fileName = DateTime.Now.ToString("MM-dd-yyy-h-mm-tt") + ".csv";
            Windows.Storage.StorageFile newFile = await Windows.Storage.DownloadsFolder.CreateFileAsync(fileName);
            await Windows.Storage.FileIO.WriteTextAsync(newFile, csv.ToString());
        }

        private Dictionary<string, string> GetResultPairs()
        {
            var locationToBox = boxToClosestLocation.ToDictionary(x => x.Value, x => x.Key);
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
                        catch (Exception err)
                        {
                            System.Diagnostics.Debug.WriteLine(err.ToString());
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
                                else
                                {
                                    box_list.Add((result.Text, FindCentroid(result)));
                                }
                            }

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

                        }

                        loop_info += "pairing: \n";
                        loop_info += PrintPairDictionary();
                    }
                    catch (Exception err)
                    {
                        System.Diagnostics.Debug.WriteLine(err.ToString());
                    }

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
                catch (Exception err)
                {
                    System.Diagnostics.Debug.WriteLine(err.ToString());
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
                catch (Exception err)
                {
                    System.Diagnostics.Debug.WriteLine(err.ToString());
                }
            }

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
                catch (Exception err)
                {
                    System.Diagnostics.Debug.WriteLine(err.ToString());
                }
            });
        }

        // extra copy from microsoft UWPSample
        private async Task ProcessSoftwareBitmap(SoftwareBitmap bitmap)
        {

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
                System.Diagnostics.Debug.WriteLine(err.ToString());
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
                catch (Exception err)
                {
                    System.Diagnostics.Debug.WriteLine(err.ToString());
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
