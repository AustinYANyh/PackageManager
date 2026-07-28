using System.Collections.Generic;
using Newtonsoft.Json;

namespace PackageManager.Features.MimoUsage.Dto
{
    /// <summary>
    /// MiMo API 通用响应包装。
    /// </summary>
    public class MimoUsageResponse<T>
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("data")]
        public T Data { get; set; }
    }

    /// <summary>
    /// 每日 Token 用量条目。
    /// </summary>
    public class MimoUsageItem
    {
        [JsonProperty("date")]
        public string Date { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("totalToken")]
        public long TotalToken { get; set; }

        [JsonProperty("inputHitToken")]
        public long InputHitToken { get; set; }

        [JsonProperty("inputMissToken")]
        public long InputMissToken { get; set; }

        [JsonProperty("outputToken")]
        public long OutputToken { get; set; }

        [JsonProperty("requestCount")]
        public int RequestCount { get; set; }

        [JsonProperty("inputAudioDuration")]
        public int InputAudioDuration { get; set; }

        /// <summary>
        /// 缓存命中率（百分比）。
        /// </summary>
        public double CacheHitRate =>
            (InputHitToken + InputMissToken) > 0
                ? (double)InputHitToken / (InputHitToken + InputMissToken) * 100
                : 0;
    }

    /// <summary>
    /// 套餐详情。
    /// </summary>
    public class MimoPlanDetail
    {
        [JsonProperty("planCode")]
        public string PlanCode { get; set; }

        [JsonProperty("planName")]
        public string PlanName { get; set; }

        [JsonProperty("currentPeriodEnd")]
        public string CurrentPeriodEnd { get; set; }

        [JsonProperty("expired")]
        public bool Expired { get; set; }
    }

    /// <summary>
    /// 月度总用量数据。
    /// </summary>
    public class MimoMonthlyUsage
    {
        [JsonProperty("percent")]
        public double Percent { get; set; }

        [JsonProperty("items")]
        public List<MimoMonthlyUsageItem> Items { get; set; }
    }

    /// <summary>
    /// 月度用量条目。
    /// </summary>
    public class MimoMonthlyUsageItem
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("used")]
        public long Used { get; set; }

        [JsonProperty("limit")]
        public long Limit { get; set; }

        [JsonProperty("percent")]
        public double Percent { get; set; }
    }
}
