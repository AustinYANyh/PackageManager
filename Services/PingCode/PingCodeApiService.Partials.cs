using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PackageManager.Services.PingCode
{
    /// <summary>
    /// PingCode 开放接口客户端的辅助定义（枚举与内部类型）。
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
