using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LumINE.Utils
{
    public class LineServiceError : Exception
    {
        public int Code { get; set; }
        public string ErrorMessage { get; set; }
        public LineServiceError(int code, string message) : base(string.Format("[code={0}] {1}", code, message))
        {
            Code = code;
            ErrorMessage = message;
        }
    }
}

