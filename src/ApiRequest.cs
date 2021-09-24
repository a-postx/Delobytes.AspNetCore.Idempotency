using System;
using System.Collections.Generic;

namespace Delobytes.AspNetCore.Idempotency
{
    /// <summary>
    /// Модель веб-запроса и результата.
    /// </summary>
    [Serializable]
    public class ApiRequest
    {
        public ApiRequest() { }

        public string ApiRequestID { get; set; }
        public string Method { get; set; }
        public string Path { get; set; }
        public string Query { get; set; }
        public int? StatusCode { get; set; }
        public Dictionary<string, List<string>> Headers { get; set; }
        public string BodyType { get; set; }
        public byte[] Body { get; set; }
        public string ResultType { get; set; }
        public string ResultRouteName { get; set; }
        public Dictionary<string, string> ResultRouteValues { get; set; }
    }
}
