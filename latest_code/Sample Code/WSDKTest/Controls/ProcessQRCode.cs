using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace WSDKTest.Controls
{
    static class ProcessQRCode
    {
        // Static method of Author 
        public static bool IsLocation(string str)
        {
            //return Regex.IsMatch(str, "[A-Za-z]\\d{7}");
            return Regex.IsMatch(str, "^[A-Z][A-Z]\\d+");
            //return str.Contains("ocation");
        }
    }
}
