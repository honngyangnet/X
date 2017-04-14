﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NewLife.Data;
using NewLife.Messaging;
using NewLife.Net;
using NewLife.Reflection;
using NewLife.Threading;

namespace NewLife.Remoting
{
    /// <summary>应用接口客户端</summary>
    public class ApiClient : ApiHost, IApiSession
    {
        #region 静态
        /// <summary>协议到提供者类的映射</summary>
        public static IDictionary<String, Type> Providers { get; } = new Dictionary<String, Type>(StringComparer.OrdinalIgnoreCase);

        static ApiClient()
        {
            var ps = Providers;
            ps.Add("tcp", typeof(ApiNetClient));
            ps.Add("udp", typeof(ApiNetClient));
            ps.Add("http", typeof(ApiHttpClient));
            ps.Add("ws", typeof(ApiHttpClient));
        }
        #endregion

        #region 属性
        /// <summary>是否已打开</summary>
        public Boolean Active { get; set; }

        /// <summary>通信客户端</summary>
        public IApiClient Client { get; set; }

        /// <summary>所有服务器所有会话，包含自己</summary>
        IApiSession[] IApiSession.AllSessions { get { return new IApiSession[] { this }; } }
        #endregion

        #region 构造
        /// <summary>实例化应用接口客户端</summary>
        public ApiClient()
        {
            var type = GetType();
            Name = type.GetDisplayName() ?? type.Name.TrimEnd("Client");

            Register(new ApiController { Host = this }, null);
        }

        /// <summary>实例化应用接口客户端</summary>
        public ApiClient(String uri) : this()
        {
            SetRemote(uri);
        }

        /// <summary>销毁</summary>
        /// <param name="disposing"></param>
        protected override void OnDispose(Boolean disposing)
        {
            base.OnDispose(disposing);

            Close(Name + (disposing ? "Dispose" : "GC"));
        }
        #endregion

        #region 打开关闭
        /// <summary>打开客户端</summary>
        public virtual Boolean Open()
        {
            if (Active) return true;

            if (Client == null) throw new ArgumentNullException(nameof(Client), "未指定通信客户端");
            //if (Encoder == null) throw new ArgumentNullException(nameof(Encoder), "未指定编码器");

            if (Encoder == null) Encoder = new JsonEncoder();
            if (Handler == null) Handler = new ApiHandler { Host = this };

#if DEBUG
            Client.Log = Log;
            Encoder.Log = Log;
#endif

            // 设置过滤器
            SetFilter();

            Client.Opened += Client_Opened;
            if (!Client.Open()) return false;

            var ms = Manager.Services;
            if (ms.Count > 0)
            {
                Log.Info("客户端可用接口{0}个：", ms.Count);
                foreach (var item in ms)
                {
                    Log.Info("\t{0,-16}{1}", item.Key, item.Value);
                }
            }

            // 打开连接后马上就可以登录
            Timer = new TimerX(OnTimer, this, 0, 30000);

            return Active = true;
        }

        /// <summary>关闭</summary>
        /// <param name="reason">关闭原因。便于日志分析</param>
        /// <returns>是否成功</returns>
        public virtual void Close(String reason)
        {
            if (!Active) return;

            Key = null;
            Timer.TryDispose();

            var tc = Client;
            if (tc != null)
            {
                tc.Opened -= Client_Opened;
                tc.Close(reason ?? (GetType().Name + "Close"));
            }

            Active = false;
        }

        /// <summary>打开后触发。</summary>
        public event EventHandler Opened;

        private void Client_Opened(Object sender, EventArgs e)
        {
            Opened?.Invoke(this, e);
        }

        /// <summary>设置远程地址</summary>
        /// <param name="uri"></param>
        /// <returns></returns>
        public Boolean SetRemote(String uri)
        {
            Type type;
            var nu = new NetUri(uri);
            if (!Providers.TryGetValue(nu.Protocol, out type)) return false;

            var ac = type.CreateInstance() as IApiClient;
            if (ac != null && ac.Init(uri))
            {
                ac.Provider = this;

                Client.TryDispose();
                Client = ac;
            }

            return true;
        }

        /// <summary>查找Api动作</summary>
        /// <param name="action"></param>
        /// <returns></returns>
        public virtual ApiAction FindAction(String action) { return Manager.Find(action); }

        /// <summary>创建控制器实例</summary>
        /// <param name="api"></param>
        /// <returns></returns>
        public virtual Object CreateController(ApiAction api) { return this.CreateController(this, api); }
        #endregion

        #region 远程调用
        ///// <summary>控制器前缀</summary>
        //public String Controller { get; set; }

        /// <summary>调用</summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="action"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public virtual async Task<TResult> InvokeAsync<TResult>(String action, Object args = null)
        {
            var ss = Client;
            if (ss == null) return default(TResult);

            if (!Logined && action != LoginAction) await LoginAsync();

            try
            {
                return await ApiHostHelper.InvokeAsync<TResult>(this, this, action, args).ConfigureAwait(false);
            }
            // 截断任务取消异常，避免过长
            catch (TaskCanceledException)
            {
                throw new TaskCanceledException(action + "超时取消");
            }
        }

        /// <summary>创建消息</summary>
        /// <param name="pk"></param>
        /// <returns></returns>
        IMessage IApiSession.CreateMessage(Packet pk) { return Client?.CreateMessage(pk); }

        async Task<IMessage> IApiSession.SendAsync(IMessage msg) { return await Client.SendAsync(msg).ConfigureAwait(false); }
        #endregion

        #region 登录
        /// <summary>用户名</summary>
        public String UserName { get; set; }

        /// <summary>密码</summary>
        public String Password { get; set; }

        /// <summary>是否已登录</summary>
        public Boolean Logined { get; protected set; }

        /// <summary>登录动作名</summary>
        public String LoginAction { get; set; } = "Login";

        /// <summary>登录完成事件</summary>
        public EventHandler<EventArgs<Object>> OnLogined;

        private Task<Object> _login;

        /// <summary>异步登录</summary>
        public virtual async Task<Object> LoginAsync()
        {
            // 同时只能发起一个登录请求
            var task = _login;
            if (task != null) return await task;

            lock (LoginAction)
            {
                task = _login;
                if (task == null) _login = task = OnLoginAsync();
            }

            try
            {
                return await task;
            }
            finally
            {
                _login = null;
            }
        }

        private async Task<Object> OnLoginAsync()
        {
            var args = OnPreLogin();

            // 登录前清空密钥
            if (Logined) Key = null;

            var rs = await OnLogin(args);

            // 从响应中解析通信密钥
            if (Encrypted)
            {
                var dic = rs.ToDictionary();
                //!!! 使用密码解密通信密钥
                Key = (dic["Key"] + "").ToHex().RC4(Password.GetBytes());
                //Key = (dic["Key"] + "").ToHex();

                WriteLog("密匙:{0}", Key.ToHex());
            }

            Logined = true;

            OnLogined?.Invoke(this, new EventArgs<Object>(rs));

            // 尽快开始一次心跳
            Timer.SetNext(1000);

            return rs;
        }

        /// <summary>预登录，默认MD5哈希密码，可继承修改登录参数构造方式</summary>
        /// <returns></returns>
        protected virtual Object OnPreLogin()
        {
            //!!! 安全起见，强烈建议不用传输明文密码
            var user = UserName;
            var pass = Password.MD5();
            if (user.IsNullOrEmpty()) throw new ArgumentNullException(nameof(user), "用户名不能为空！");
            //if (pass.IsNullOrEmpty()) throw new ArgumentNullException(nameof(pass), "密码不能为空！");

            return new { user, pass };
        }

        /// <summary>执行登录，可继承修改登录动作</summary>
        /// <param name="args"></param>
        /// <returns></returns>
        protected virtual async Task<Object> OnLogin(Object args)
        {
            return await InvokeAsync<Object>(LoginAction, args);
        }
        #endregion

        #region 心跳
        /// <summary>调用响应延迟，毫秒</summary>
        public Int32 Delay { get; private set; }

        /// <summary>服务端与客户端的时间差</summary>
        public TimeSpan Span { get; private set; }

        /// <summary>心跳动作名</summary>
        public String PingAction { get; set; } = "Ping";

        /// <summary>发送心跳</summary>
        /// <param name="args"></param>
        /// <returns>是否收到成功响应</returns>
        public virtual async Task<Object> PingAsync(Object args = null)
        {
            var dic = args.ToDictionary();
            if (!dic.ContainsKey("Time")) dic["Time"] = DateTime.Now;

            var rs = await InvokeAsync<Object>(PingAction, dic);
            dic = rs.ToDictionary();

            // 加权计算延迟
            Object obj;
            if (dic.TryGetValue("Time", out obj))
            {
                var ts = DateTime.Now - obj.ToDateTime();
                var ms = (Int32)ts.TotalMilliseconds;

                if (Delay <= 0)
                    Delay = ms;
                else
                    Delay = (Delay * 3 + ms) / 4;
            }

            // 获取服务器时间
            if (dic.TryGetValue("ServerTime", out obj))
            {
                // 保存时间差，这样子以后只需要拿本地当前时间加上时间差，即可得到服务器时间
                Span = DateTime.Now - obj.ToDateTime();
                //WriteLog("时间差：{0}ms", (Int32)Span.TotalMilliseconds);
            }

            return rs;
        }

        /// <summary>定时器</summary>
        protected TimerX Timer { get; set; }

        /// <summary>定时执行登录或心跳</summary>
        /// <param name="state"></param>
        protected async void OnTimer(Object state)
        {
            try
            {
                if (Logined)
                    await PingAsync();
                else if (!UserName.IsNullOrEmpty())
                    await LoginAsync();
            }
            catch (TaskCanceledException ex) { Log.Error(ex.Message); }
            catch (ApiException ex) { Log.Error(ex.Message); }
            catch (Exception ex) { Log.Error(ex.ToString()); }
        }
        #endregion

        #region 加密&压缩
        /// <summary>加密通信指令中负载数据的密匙</summary>
        public Byte[] Key { get; set; }

        /// <summary>获取通信密钥的委托</summary>
        /// <returns></returns>
        protected override Func<FilterContext, Byte[]> GetKeyFunc() { return ctx => Key; }
        #endregion

        #region 服务提供者
        /// <summary>获取服务提供者</summary>
        /// <param name="serviceType"></param>
        /// <returns></returns>
        public override Object GetService(Type serviceType)
        {
            if (serviceType == GetType()) return this;
            if (serviceType == typeof(IApiClient)) return Client;

            return base.GetService(serviceType);
        }
        #endregion
    }
}