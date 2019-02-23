using CustomVision;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.AI.MachineLearning;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using System.Text.RegularExpressions;
using WSDKTest;

namespace DJIDemo.AIModel
{
    class ProcessWithONNX
    {
        SolidColorBrush _fillBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);
        SolidColorBrush _lineBrushRed = new SolidColorBrush(Windows.UI.Colors.Red);
        SolidColorBrush _lineBrushGreen = new SolidColorBrush(Windows.UI.Colors.Green);
        double _lineThickness = 2.0;
        StorageFile file = null;


        ObjectDetection objectDetection ;
        public ProcessWithONNX()
        {
            List<String> labels = new List<String> {"box"};
            objectDetection = new ObjectDetection(labels,20,0.4F,0.45F);
 
        }
    
        public  async Task<string> ProcessSoftwareBitmap(SoftwareBitmap bitmap, SoftwareBitmap originalBitmap,  MainPage mainPage)
        {

            string ret = null;
            if (!Windows.Foundation.Metadata.ApiInformation.IsMethodPresent("Windows.Media.VideoFrame", "CreateWithSoftwareBitmap"))
            {
                return "hi\n";
            }
            //Convert SoftwareBitmap  into VideoFrame
            using (VideoFrame frame = VideoFrame.CreateWithSoftwareBitmap(bitmap))
            {

                try
                {
                    if (file == null)
                    {
                        file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///AIModel/model.onnx"));
                        await objectDetection.Init(file);
                    }

                    var output = await objectDetection.PredictImageAsync(frame);
                    
                    if (output != null)
                    {
                        mainPage.UpdateTextbox("output is not null.\n");
                        ret = await UpdateResult(output, mainPage, bitmap, originalBitmap);
                    } else
                    {
                        mainPage.UpdateTextbox("No result.\n");
                    }



                }
                catch (Exception e)
                {
                    string s = e.Message;
                    mainPage.ShowMessagePopup(e.Message);
                }

            }

            return ret;
        }

        private async Task<string> UpdateResult(IList<PredictionModel> outputlist, MainPage mainPage, SoftwareBitmap bitmap, SoftwareBitmap originalBitmap)
        {
            string resultText = "";
            resultText += "result count: "+outputlist.Count+"\n";
            return resultText;
            //uint pixelWidth = (uint)bitmap.PixelWidth;
            //uint pixelHeight = (uint)bitmap.PixelHeight;
            try
            {
                await mainPage.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    var overlayCanvas = mainPage.GetCanvas();

                    var VideoActualWidth = (uint)mainPage.GetWidth();
                    var VideoActualHeight = (uint)mainPage.GetHeight();


                    overlayCanvas.Children.Clear();
                    
                    foreach (var output in outputlist)
                    {

                        var box = output.BoundingBox;

                       
                        double x = (double)Math.Max(box.Left, 0);
                        double y = (double)Math.Max(box.Top, 0);
                        double w = (double)Math.Min(1 - x, box.Width);
                        double h = (double)Math.Min(1 - y, box.Height);

                        var bitmap4barcode = await GetCroppedBitmapAsync(originalBitmap, (uint)(x * originalBitmap.PixelWidth),
                                                                                (uint)(y * originalBitmap.PixelHeight),
                                                                                (uint)(w * originalBitmap.PixelWidth),
                                                                                (uint)(h * originalBitmap.PixelHeight));
                        var result = mainPage.DecodeQRCode(bitmap4barcode);

                        var resultLen = result.Length;
                        var locationNum = 0;
                        string locationID = null;
                        string boxID = null;
                        if (result != null && resultLen > 0)
                        {
                            foreach (var r in result)
                            {
                                if (IsLocation(r.Text))
                                {
                                    locationID = r.Text;
                                    locationNum++;
                                } else
                                {
                                    boxID = r.Text;
                                }
                                
                            }
                            if (locationNum == 1 && resultLen == 2) // one location and one box
                            {
                                mainPage.matchedPairs[locationID] = boxID;
                                resultText += ("Location: " + locationID + ", " + "Box: " + boxID + "\n");
                            } else if (locationNum == resultLen) // all locations
                            {
                                foreach(var r in result)
                                {
                                    mainPage.matchedPairs[r.Text] = null;
                                }
                            }
                        }
                        //var barcodeoutput = await processWithBarcode.ProcessSoftwareBitmap(bitmap4barcode, Mainpage);

                        bitmap4barcode.Dispose();

                        //string boxTest = output.TagName;

                        //x = VideoActualWidth * x;
                        //y = VideoActualHeight * y;
                        //w = VideoActualWidth * w;
                        //h = VideoActualHeight * h;
                        
                        //var rectStroke = boxTest == "person"? _lineBrushGreen: _lineBrushRed;

                        //var r = new Windows.UI.Xaml.Shapes.Rectangle
                        //{
                        //    Tag = box,
                        //    Width = w,
                        //    Height = h,
                        //    Fill = _fillBrush,
                        //    Stroke = rectStroke,
                        //    StrokeThickness = _lineThickness,
                        //    Margin = new Thickness(x, y, 0, 0)
                        //};



                        //var tb = new TextBlock
                        //{
                        //    Margin = new Thickness(x + 4, y + 4, 0, 0),
                        //    Text = $"{boxTest} ({Math.Round(output.Probability, 4)})",
                        //    FontWeight = FontWeights.Bold,
                        //    Width = 126,
                        //    Height = 21,
                        //    HorizontalTextAlignment = TextAlignment.Center
                        //};

                        //var textBack = new Windows.UI.Xaml.Shapes.Rectangle
                        //{
                        //    Width = 134,
                        //    Height = 29,
                        //    Fill = rectStroke,
                        //    Margin = new Thickness(x, y, 0, 0)
                        //};

                        //overlayCanvas.Children.Add(textBack);
                        //overlayCanvas.Children.Add(tb);
                        //overlayCanvas.Children.Add(r);
                    }
                });
            }
            catch (Exception ex)
            {
                mainPage.ShowMessagePopup(ex.Message);

            }
            return resultText;
        }

        public bool IsLocation(string str)
        {
            //return Regex.IsMatch(str, "[A-Za-z]\\d{7}");
            return str.Contains("ocation");
        }

        public async static Task<SoftwareBitmap> GetCroppedBitmapAsync(
            SoftwareBitmap softwareBitmap,
            uint startPointX,
            uint startPointY,
            uint width,
            uint height)
        {
            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);

                encoder.SetSoftwareBitmap(SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8));

                encoder.BitmapTransform.Bounds = new BitmapBounds()
                {
                    X = startPointX,
                    Y = startPointY,
                    Height = height,
                    Width = width
                };

                await encoder.FlushAsync();

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

                return await decoder.GetSoftwareBitmapAsync(softwareBitmap.BitmapPixelFormat, softwareBitmap.BitmapAlphaMode);
            }
        }

    }
}
