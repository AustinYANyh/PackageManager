using System;

namespace PackageManager.Views
{
    /// <summary>
    /// 中央区域页面接口，定义页面请求退出（返回主页）的事件。
    /// </summary>
    public interface ICentralPage
    {
        /// <summary>
        /// 页面请求退出时触发。
        /// </summary>
        event Action RequestExit;
    }
}

