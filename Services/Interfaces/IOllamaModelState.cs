namespace ChizuChan.Services.Interfaces
{
    public interface IOllamaModelState
    {
        string CurrentModel { get; }
        bool IsVisionModel { get; }
        void SetModel(string model);
    }
}
