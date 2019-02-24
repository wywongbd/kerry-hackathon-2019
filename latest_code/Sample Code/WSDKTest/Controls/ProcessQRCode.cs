using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using ZXing;

namespace WSDKTest.Controls
{
    static class ProcessQRCode
    {
        // Static method of Author 
        public static bool IsLocation(string str)
        {
            //return Regex.IsMatch(str, "[A-Za-z]\\d{7}");
            return Regex.IsMatch(str, "^[A-Z][A-Z]\\d+");
        }

        public static bool IsBox(string str)
        {
            return Regex.IsMatch(str, "^[0-9]+$");
        }

        public static bool DetectEnd(Result[] r_list)
        {
            foreach(var r in r_list)
            {
                if (r.Text.Contains("Item") || r.Text.Contains("Location"))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
