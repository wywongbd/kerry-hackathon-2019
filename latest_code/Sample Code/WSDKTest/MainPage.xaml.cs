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
                        if (results != null && results.Length > 0)
                        {
                            //// distinguish location and non location result.
                            //foreach (var result in results)
                            //{
                            //    if(ProcessQRCode.IsLocation(result.Text)) {
                            //}

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

                    var attitude_result = await DJISDKManager.Instance.ComponentManager.GetFlightControllerHandler(0, 0).GetAttitudeAsync();
                    loop_info = "attitude: "+attitude_result.ToString();

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
