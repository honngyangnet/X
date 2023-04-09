﻿using System.Web;
using NewLife.Data;
using NewLife.Http;
using NewLife.Log;
using NewLife.Remoting;

namespace NewLife.IO;

/// <summary>文件存储客户端</summary>
/// <remarks>
/// 使用方式，可以引用sdk，也可以直接把 EasyClient 类抠出来使用。
/// </remarks>
public class EasyClient : IObjectStorage
{
    #region 属性
    /// <summary>服务端地址。包括/io的控制器路径</summary>
    public String Server { get; set; }

    /// <summary>应用标识</summary>
    public String AppId { get; set; }

    /// <summary>应用密钥</summary>
    public String Secret { get; set; }

    /// <summary>是否支持获取文件直接访问Url</summary>
    public Boolean CanGetUrl => true;

    /// <summary>是否支持删除</summary>
    public Boolean CanDelete => true;

    /// <summary>是否支持搜索</summary>
    public Boolean CanSearch => true;

    ///// <summary>批大小。搜索等批操作时使用</summary>
    //public Int32 BatchSize { get; set; } = 100;

    private ApiHttpClient _client;
    #endregion

    #region 基础方法
    /// <summary>获取客户端</summary>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    protected virtual ApiHttpClient GetClient()
    {
        if (_client == null)
        {
            if (Server.IsNullOrEmpty()) throw new ArgumentNullException(nameof(Server));
            //if (AppId.IsNullOrEmpty()) throw new ArgumentNullException(nameof(AppId));

            // 支持多服务器地址，支持负载均衡
            var client = new ApiHttpClient(Server);

            if (!AppId.IsNullOrEmpty())
                client.Filter = new TokenHttpFilter { UserName = AppId, Password = Secret };

            _client = client;
        }

        return _client;
    }
    #endregion

    #region 文件管理
    /// <summary>上传对象</summary>
    /// <param name="id">对象标识。支持斜杠目录结构</param>
    /// <param name="data"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public virtual async Task<IObjectInfo> Put(String id, Packet data)
    {
        if (id.IsNullOrEmpty()) throw new ArgumentNullException(nameof(id));

        var client = GetClient();
        var rs = await client.PutAsync<ObjectInfo>($"Put?id={HttpUtility.UrlEncode(id)}", data);
        rs.Data ??= data;

        return rs;
    }

    /// <summary>根据Id获取对象</summary>
    /// <param name="id">对象标识。支持斜杠目录结构</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public virtual async Task<IObjectInfo> Get(String id)
    {
        if (id.IsNullOrEmpty()) throw new ArgumentNullException(nameof(id));

        var client = GetClient();
        var rs = await client.GetAsync<Packet>("Get", new { id });

        return new ObjectInfo { Name = id, Data = rs };
    }

    /// <summary>获取对象下载Url</summary>
    /// <param name="id">对象标识。支持斜杠目录结构</param>
    /// <returns></returns>
    public virtual async Task<String> GetUrl(String id)
    {
        if (id.IsNullOrEmpty()) throw new ArgumentNullException(nameof(id));

        var client = GetClient();
        return await client.GetAsync<String>("GetUrl", new { id });
    }

    /// <summary>删除文件对象</summary>
    /// <param name="id">对象文件名</param>
    /// <returns></returns>
    public virtual async Task<Int32> Delete(String id)
    {
        if (id.IsNullOrEmpty()) throw new ArgumentNullException(nameof(id));

        var client = GetClient();
        return await client.DeleteAsync<Int32>("Delete", new { id });
    }

    /// <summary>搜索文件</summary>
    /// <param name="pattern">匹配模式。如/202304/*.jpg</param>
    /// <param name="start">开始序号。0开始</param>
    /// <param name="count">最大个数</param>
    /// <returns></returns>
    public virtual async Task<IList<IObjectInfo>> Search(String pattern = null, Int32 start = 0, Int32 count = 100)
    {
        //if (searchPattern.IsNullOrEmpty()) throw new ArgumentNullException(nameof(searchPattern));

        var client = GetClient();
        var rs = await client.GetAsync<IList<ObjectInfo>>("Search", new { pattern, start, count });
        return rs?.Cast<IObjectInfo>().ToList();
    }
    #endregion

    #region 辅助
    /// <summary>性能追踪</summary>
    public ITracer Tracer { get; set; }

    /// <summary>日志</summary>
    public ILog Log { get; set; }

    /// <summary>写日志</summary>
    /// <param name="format"></param>
    /// <param name="args"></param>
    public void WriteLog(String format, params Object[] args) => Log?.Info(format, args);
    #endregion
}