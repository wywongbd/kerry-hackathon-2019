using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.AI.MachineLearning;
namespace WSDKTest
{
    
    public sealed class TinyYOLOInput
    {
        public ImageFeatureValue data; // BitmapPixelFormat: Rgba8, BitmapAlphaMode: Premultiplied, width: 416, height: 416
    }
    
    public sealed class TinyYOLOOutput
    {
        public TensorFloat model_outputs0; // shape(-1,125,13,13)
    }
    
    public sealed class TinyYOLOModel
    {
        private LearningModel model;
        private LearningModelSession session;
        private LearningModelBinding binding;
        public static async Task<TinyYOLOModel> CreateFromStreamAsync(IRandomAccessStreamReference stream)
        {
            TinyYOLOModel learningModel = new TinyYOLOModel();
            learningModel.model = await LearningModel.LoadFromStreamAsync(stream);
            learningModel.session = new LearningModelSession(learningModel.model);
            learningModel.binding = new LearningModelBinding(learningModel.session);
            return learningModel;
        }
        public async Task<TinyYOLOOutput> EvaluateAsync(TinyYOLOInput input)
        {
            binding.Bind("data", input.data);
            var result = await session.EvaluateAsync(binding, "0");
            var output = new TinyYOLOOutput();
            output.model_outputs0 = result.Outputs["model_outputs0"] as TensorFloat;
            return output;
        }
    }
}
