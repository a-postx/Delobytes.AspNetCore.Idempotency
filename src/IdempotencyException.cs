using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Delobytes.AspNetCore.Idempotency
{
    public class IdempotencyException : Exception
    {
        public IdempotencyException()
        {
        }

        public IdempotencyException(string message) : base(message)
        {
        }

        public IdempotencyException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
