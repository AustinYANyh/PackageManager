using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PackageManager.Services.PingCode
{
    /// <summary>
    /// PingCodeApiService 的辅助定义
    /// </summary>
    public partial class PingCodeApiService
    {
        private enum PriorityCategory
        {
            Highest,
            Higher,
            Other,
        }
    }
}
