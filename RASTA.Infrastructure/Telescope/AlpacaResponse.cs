namespace RASTA.Infrastructure.Telescope
{
    public class AlpacaResponse<T>
    {
        public T Value { get; set; }
        public int ErrorNumber { get; set; }
        public string ErrorMessage { get; set; }
    }
}
