﻿using System;
using System.Threading.Tasks;
using NewLife.Collections;
using NewLife.Data;
using NewLife.Log;
using NewLife.Messaging;
using NewLife.Net;
using NewLife.Threading;
#if !NET4
using TaskEx = System.Threading.Tasks.Task;
#endif

namespace NewLife.Remoting
{
    /// <summary>应用接口客户端</summary>
    public class ApiClient : ApiHost/*, IApiSession*/
    {
        #region 属性
        /// <summary>是否已打开</summary>
        public Boolean Active { get; protected set; }

        /// <summary>服务端地址集合。负载均衡</summary>
        public String[] Servers { get; set; }

        /// <summary>客户端连接集群</summary>
        public ICluster<String, ISocketClient> Cluster { get; set; }

        /// <summary>是否使用连接池。true时建立多个到服务端的连接（高吞吐），默认false使用单一连接（低延迟）</summary>
        public Boolean UsePool { get; set; }

        ///// <summary>主机</summary>
        //IApiHost IApiSession.Host => this;

        /// <summary>最后活跃时间</summary>
        public DateTime LastActive { get; set; }

        ///// <summary>所有服务器所有会话，包含自己</summary>
        //IApiSession[] IApiSession.AllSessions => new IApiSession[] { this };

        /// <summary>调用统计</summary>
        public ICounter StatInvoke { get; set; }

        /// <summary>发送数据包统计信息</summary>
        public ICounter StatSend { get; set; }

        /// <summary>接收数据包统计信息</summary>
        public ICounter StatReceive { get; set; }
        #endregion

        #region 构造
        /// <summary>实例化应用接口客户端</summary>
        public ApiClient()
        {
            var type = GetType();
            Name = type.GetDisplayName() ?? type.Name.TrimEnd("Client");

            //// 注册默认服务控制器
            //Register(new ApiController { Host = this }, null);
        }

        /// <summary>实例化应用接口客户端</summary>
        /// <param name="uris">服务端地址集合，逗号分隔</param>
        public ApiClient(String uris) : this()
        {
            if (!uris.IsNullOrEmpty()) Servers = uris.Split(",", ";");
        }

        /// <summary>销毁</summary>
        /// <param name="disposing"></param>
        protected override void Dispose(Boolean disposing)
        {
            base.Dispose(disposing);

            _Timer.TryDispose();

            Close(Name + (disposing ? "Dispose" : "GC"));
        }
        #endregion

        #region 打开关闭
        private readonly Object Root = new Object();
        /// <summary>打开客户端</summary>
        public virtual Boolean Open()
        {
            if (Active) return true;
            lock (Root)
            {
                if (Active) return true;

                var ss = Servers;
                if (ss == null || ss.Length == 0) throw new ArgumentNullException(nameof(Servers), "未指定服务端地址");

                if (Encoder == null) Encoder = new JsonEncoder();
                //if (Encoder == null) Encoder = new BinaryEncoder();
                if (Handler == null) Handler = new ApiHandler { Host = this };

                // 集群
                Cluster = InitCluster();
                WriteLog("集群：{0}", Cluster);

                Encoder.Log = EncoderLog;

                //// 拥有默认服务控制器之外的服务时，才显示服务
                //var svcs = Manager.Services;
                //if (svcs.Any(e => !(e.Value.Controller is ApiController))) ShowService();

                // 控制性能统计信息
                var ms = StatPeriod * 1000;
                if (ms > 0)
                {
                    //if (StatInvoke == null) StatInvoke = new PerfCounter();
                    //if (StatProcess == null) StatProcess = new PerfCounter();
                    if (StatSend == null) StatSend = new PerfCounter();
                    if (StatReceive == null) StatReceive = new PerfCounter();

                    _Timer = new TimerX(DoWork, null, ms, ms) { Async = true };
                }

                return Active = true;
            }
        }

        /// <summary>关闭</summary>
        /// <param name="reason">关闭原因。便于日志分析</param>
        /// <returns>是否成功</returns>
        public virtual void Close(String reason)
        {
            if (!Active) return;

            Cluster?.Close(reason ?? (GetType().Name + "Close"));

            Active = false;
        }

        /// <summary>初始化集群</summary>
        protected virtual ICluster<String, ISocketClient> InitCluster()
        {
            var cluster = Cluster;
            if (cluster == null)
            {
                if (UsePool)
                    cluster = new ClientPoolCluster { Log = Log };
                else
                    cluster = new ClientSingleCluster { Log = Log };
                //Cluster = cluster;
            }

            if (cluster is ClientSingleCluster sc && sc.OnCreate == null) sc.OnCreate = OnCreate;
            if (cluster is ClientPoolCluster pc && pc.OnCreate == null) pc.OnCreate = OnCreate;

            if (cluster.GetItems == null) cluster.GetItems = () => Servers;
            cluster.Open();

            return cluster;
        }

        ///// <summary>查找Api动作</summary>
        ///// <param name="action"></param>
        ///// <returns></returns>
        //public virtual ApiAction FindAction(String action) => Manager.Find(action);

        ///// <summary>创建控制器实例</summary>
        ///// <param name="api"></param>
        ///// <returns></returns>
        //public virtual Object CreateController(ApiAction api) => this.CreateController(this, api);
        #endregion

        #region 远程调用
        /// <summary>异步调用，等待返回结果</summary>
        /// <param name="resultType">返回类型</param>
        /// <param name="action">服务操作</param>
        /// <param name="args">参数</param>
        /// <param name="flag">标识</param>
        /// <returns></returns>
        public virtual async Task<Object> InvokeAsync(Type resultType, String action, Object args = null, Byte flag = 0)
        {
            // 让上层异步到这直接返回，后续代码在另一个线程执行
            //!!! Task.Yield会导致强制捕获上下文，虽然会在另一个线程执行，但在UI线程中可能无法抢占上下文导致死锁
#if !NET4
            //await Task.Yield();
#endif

            Open();

            var act = action;

            try
            {
                return await InvokeAsync(this, resultType, act, args, flag).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                // 重新登录后再次调用
                if (ex.Code == 401)
                {
                    await Cluster.InvokeAsync(client => OnLoginAsync(client, true)).ConfigureAwait(false);

                    return await InvokeAsync(this, resultType, act, args, flag).ConfigureAwait(false);
                }

                throw;
            }
            // 截断任务取消异常，避免过长
            catch (TaskCanceledException)
            {
                throw new TaskCanceledException($"[{act}]超时[{Timeout:n0}ms]取消");
            }
        }

        /// <summary>异步调用，等待返回结果</summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="action">服务操作</param>
        /// <param name="args">参数</param>
        /// <param name="flag">标识</param>
        /// <returns></returns>
        public virtual async Task<TResult> InvokeAsync<TResult>(String action, Object args = null, Byte flag = 0)
        {
            // 发送失败时，返回空
            var rs = await InvokeAsync(typeof(TResult), action, args, flag).ConfigureAwait(false);
            if (rs == null) return default;

            return (TResult)rs;
        }

        /// <summary>同步调用，阻塞等待</summary>
        /// <param name="action">服务操作</param>
        /// <param name="args">参数</param>
        /// <param name="flag">标识</param>
        /// <returns></returns>
        public virtual TResult Invoke<TResult>(String action, Object args = null, Byte flag = 0)
        {
            // 发送失败时，返回空
            var rs = TaskEx.Run(() => InvokeAsync(typeof(TResult), action, args, flag)).Result;
            if (rs == null) return default;

            return (TResult)rs;
        }

        /// <summary>单向发送。同步调用，不等待返回</summary>
        /// <param name="action">服务操作</param>
        /// <param name="args">参数</param>
        /// <param name="flag">标识</param>
        /// <returns></returns>
        public virtual Boolean InvokeOneWay(String action, Object args = null, Byte flag = 0)
        {
            if (!Open()) return false;

            var act = action;

            return Invoke(this, act, args, flag);
        }

        /// <summary>指定客户端的异步调用，等待返回结果</summary>
        /// <remarks>常用于在OnLoginAsync中实现连接后登录功能</remarks>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="client">客户端</param>
        /// <param name="action">服务操作</param>
        /// <param name="args">参数</param>
        /// <param name="flag">标识</param>
        /// <returns></returns>
        protected virtual async Task<TResult> InvokeWithClientAsync<TResult>(ISocketClient client, String action, Object args = null, Byte flag = 0)
        {
            var act = action;

            return (TResult)await InvokeAsync(client, typeof(TResult), act, args, flag).ConfigureAwait(false);
        }

        /// <summary>调用</summary>
        /// <param name="session"></param>
        /// <param name="resultType">结果类型</param>
        /// <param name="action">服务操作</param>
        /// <param name="args">参数</param>
        /// <param name="flag">标识</param>
        /// <returns></returns>
        private async Task<Object> InvokeAsync(Object session, Type resultType, String action, Object args, Byte flag)
        {
            if (session == null) return null;

            // 性能计数器，次数、TPS、平均耗时
            //host.StatSend?.Increment();
            var st = StatInvoke;
            var sw = st.StartCount();

            // 编码请求，构造消息
            var enc = Encoder;
            var msg = enc.CreateRequest(action, args);
            if (flag > 0 && msg is DefaultMessage dm) dm.Flag = flag;

            var invoker = session;
            IMessage rs = null;
            try
            {
                if (session is IApiSession ss)
                    rs = (await Cluster.InvokeAsync(client => client.SendMessageAsync(msg)).ConfigureAwait(false)) as IMessage;
                else if (session is ISocketRemote client)
                    rs = (await client.SendMessageAsync(msg).ConfigureAwait(false)) as IMessage;
                else
                    throw new InvalidOperationException();

                if (rs == null) return null;
            }
            catch (AggregateException aggex)
            {
                var ex = aggex.GetTrue();
                if (ex is TaskCanceledException)
                {
                    throw new TimeoutException($"请求[{action}]超时！", ex);
                }
                throw aggex;
            }
            catch (TaskCanceledException ex)
            {
                throw new TimeoutException($"请求[{action}]超时！", ex);
            }
            finally
            {
                var msCost = st.StopCount(sw) / 1000;
                if (SlowTrace > 0 && msCost >= SlowTrace) WriteLog($"慢调用[{action}]，耗时{msCost:n0}ms");
            }

            // 特殊返回类型
            if (resultType == typeof(IMessage)) return rs;
            //if (resultType == typeof(Packet)) return rs.Payload;

            if (!enc.Decode(rs, out _, out var code, out var data)) throw new InvalidOperationException();

            // 是否成功
            if (code != 0) throw new ApiException(code, $"远程[{invoker}]错误！ {data.ToStr()}");

            if (data == null) return null;
            if (resultType == typeof(Packet)) return data;

            // 解码结果
            var result = enc.DecodeResult(action, data);
            if (resultType == typeof(Object)) return result;

            // 返回
            return enc.Convert(result, resultType);
        }

        /// <summary>调用</summary>
        /// <param name="session"></param>
        /// <param name="action">服务操作</param>
        /// <param name="args">参数</param>
        /// <param name="flag">标识</param>
        /// <returns></returns>
        private Boolean Invoke(Object session, String action, Object args, Byte flag = 0)
        {
            if (session == null) return false;

            // 性能计数器，次数、TPS、平均耗时
            var st = StatInvoke;

            // 编码请求
            var msg = Encoder.CreateRequest(action, args);

            if (msg is DefaultMessage dm)
            {
                dm.OneWay = true;
                if (flag > 0) dm.Flag = flag;
            }

            var sw = st.StartCount();
            try
            {
                if (session is IApiSession ss)
                    return Cluster.Invoke(client => client.SendMessage(msg));
                else if (session is ISocketRemote client)
                    return client.SendMessage(msg);
                else
                    throw new InvalidOperationException();
            }
            finally
            {
                var msCost = st.StopCount(sw) / 1000;
                if (SlowTrace > 0 && msCost >= SlowTrace) WriteLog($"慢调用[{action}]，耗时{msCost:n0}ms");
            }
        }

        //Task<IMessage> IApiSession.SendAsync(IMessage msg) => Cluster.InvokeAsync(client => client.SendMessageAsync(msg)).ContinueWith(t => t.Result as IMessage);

        //Boolean IApiSession.Send(IMessage msg) => Cluster.Invoke(client => client.SendMessage(msg));
        #endregion

        #region 登录
        /// <summary>新会话。客户端每次连接或断线重连后，可用InvokeWithClientAsync做登录</summary>
        /// <param name="client">会话</param>
        public virtual void OnNewSession(ISocketClient client)
        {
            //var client = state as ISocketClient;
            OnLoginAsync(client, true)?.Wait();
        }

        /// <summary>连接后自动登录</summary>
        /// <param name="client">客户端</param>
        /// <param name="force">强制登录</param>
        protected virtual Task<Object> OnLoginAsync(ISocketClient client, Boolean force) => TaskEx.FromResult<Object>(null);

        /// <summary>登录</summary>
        /// <returns></returns>
        public virtual async Task<Object> LoginAsync()
        {
#if !NET4
            //await Task.Yield();
#endif

            return await Cluster.InvokeAsync(client => OnLoginAsync(client, false)).ConfigureAwait(false);
        }
        #endregion

        #region 连接池
        /// <summary>创建客户端之后，打开连接之前</summary>
        /// <param name="svr"></param>
        protected virtual ISocketClient OnCreate(String svr)
        {
            var client = new NetUri(svr).CreateRemote();
            // 网络层采用消息层超时
            client.Timeout = Timeout;
            client.StatSend = StatSend;
            client.StatReceive = StatReceive;

            client.Add(GetMessageCodec());

            client.Opened += (s, e) => OnNewSession(s as ISocketClient);
            //client.Received += Client_Received;

            return client;
        }

        //private void Client_Received(Object sender, ReceivedEventArgs e)
        //{
        //    LastActive = DateTime.Now;

        //    // Api解码消息得到Action和参数
        //    if (!(e.Message is IMessage msg) || msg.Reply) return;

        //    var ss = sender as ISocketRemote;
        //    var host = this as IApiHost;
        //    var rs = host.Process(this, msg);
        //    if (rs != null) ss?.SendMessage(rs);
        //}
        #endregion

        #region 统计
        private TimerX _Timer;
        private String _Last;

        /// <summary>显示统计信息的周期。默认600秒，0表示不显示统计信息</summary>
        public Int32 StatPeriod { get; set; } = 600;

        private void DoWork(Object state)
        {
            var sb = Pool.StringBuilder.Get();
            var pf1 = StatInvoke;
            if (pf1 != null && pf1.Value > 0) sb.AppendFormat("请求：{0} ", pf1);

            var st1 = StatSend;
            var st2 = StatReceive;
            if (st1 != null && st1.Value > 0) sb.AppendFormat("发送：{0} ", st1);
            if (st2 != null && st2.Value > 0) sb.AppendFormat("接收：{0} ", st2);

            var msg = sb.Put(true);
            if (msg.IsNullOrEmpty() || msg == _Last) return;
            _Last = msg;

            WriteLog(msg);
        }
        #endregion
    }
}