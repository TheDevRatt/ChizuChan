using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChizuChan.DTOs
{
    public class StandardResponse<T>
    {
        public bool Success { get; set; }
        public int StatusCode { get; set; }
        public string? ErrorMessage { get; set; }
        public T? Data { get; set; }

        public static StandardResponse<T> SuccessResponse(T data, int statusCode = 200)
        {
            return new StandardResponse<T>
            {
                Success = true,
                StatusCode = statusCode,
                Data = data
            };
        }

        public static StandardResponse<T> ErrorResponse(string errorMessage, int statusCode = 500)
        {
            return new StandardResponse<T>
            {
                Success = false,
                StatusCode = statusCode,
                ErrorMessage = errorMessage
            };
        }
    }
}
