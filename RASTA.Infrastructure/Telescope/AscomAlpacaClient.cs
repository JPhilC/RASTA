using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RASTA.Infrastructure.Telescope
{
    public class AscomAlpacaClient
    {
        private readonly HttpClient _httpClient = new();
        private readonly uint _clientId;
        private uint _transactionId = 1;

        public string BaseUrl { get; set; } = string.Empty;

        public AscomAlpacaClient()
        {
            // Session-wide ClientID (1–65535)
            _clientId = (uint)Random.Shared.Next(1, 65536);
        }

        private uint NextTransactionId()
        {
            if (_transactionId == uint.MaxValue)
                _transactionId = 1;
            else
                _transactionId++;

            return _transactionId;
        }

        private string BuildUrl(string endpoint)
        {
            return $"{BaseUrl}/{endpoint}?ClientID={_clientId}&ClientTransactionID={NextTransactionId()}";
        }

        public async Task<T> GetAsync<T>(string endpoint)
        {
            var url = BuildUrl(endpoint);

            var response = await _httpClient.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<AlpacaResponse<T>>(json);

            if (result is null)
                throw new Exception($"Invalid Alpaca response from {endpoint}");

            if (result.ErrorNumber != 0)
                throw new Exception($"Alpaca error {result.ErrorNumber}: {result.ErrorMessage}");

            return result.Value;
        }

        public async Task PutAsync(string endpoint, params (string key, string value)[] parameters)
        {
            var url = BuildUrl(endpoint);

            var content = new FormUrlEncodedContent(
                parameters.Select(p => new KeyValuePair<string, string>(p.key, p.value))
            );

            var response = await _httpClient.PutAsync(url, content);
            var json = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize<AlpacaResponse<object>>(json);

            if (result is null)
                throw new Exception($"Invalid Alpaca response from {endpoint}");

            if (result.ErrorNumber != 0)
                throw new Exception($"Alpaca error {result.ErrorNumber}: {result.ErrorMessage}");
        }
    }

    public class AlpacaResponse<T>
    {
        public uint ClientTransactionID { get; set; }
        public uint ServerTransactionID { get; set; }
        public int ErrorNumber { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public T Value { get; set; }
    }

}
