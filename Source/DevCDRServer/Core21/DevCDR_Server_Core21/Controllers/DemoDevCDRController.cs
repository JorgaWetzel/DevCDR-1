﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

namespace DevCDRServer.Controllers
{
    [AllowAnonymous]
    public class DemoController : Controller
    {
        private readonly IHubContext<Demo> _hubContext;
        private readonly IHostingEnvironment _env;
        private IMemoryCache _cache;
        public DevCDR.Extensions.AzureLogAnalytics AzureLog = new DevCDR.Extensions.AzureLogAnalytics("","","");

        public DemoController(IHubContext<Demo> hubContext, IHostingEnvironment env, IMemoryCache memoryCache)
        {
            _hubContext = hubContext;
            _env = env;
            _cache = memoryCache;

            //if (string.IsNullOrEmpty(AzureLog.WorkspaceId))
            //{
            //    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Log-WorkspaceID")) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("Log-SharedKey")))
            //    {
            //        AzureLog = new DevCDR.Extensions.AzureLogAnalytics(Environment.GetEnvironmentVariable("Log-WorkspaceID"), Environment.GetEnvironmentVariable("Log-SharedKey"), "DevCDR_" + (Environment.GetEnvironmentVariable("INSTANCENAME") ?? "Default"));
            //        //AzureLog.PostAsync(new { Computer = Environment.MachineName, EventID = 0001, Description = "DevCDRController started" });
            //    }
            //}
        }

        [AllowAnonymous]
        public ActionResult Default()
        {
            ViewBag.Title = "Demo (read-only)";
            ViewBag.Instance = Environment.GetEnvironmentVariable("INSTANCENAME") ?? "Demo";
            ViewBag.appVersion = typeof(DevCDRController).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>().Version;
            ViewBag.Endpoint = Request.GetEncodedUrl().Split("/Demo/Default")[0] + "/chat";
            if (System.IO.File.Exists(Path.Combine(_env.WebRootPath, "DevCDRAgentCore.msi")))
                ViewBag.MSI = Request.GetEncodedUrl().Split("/Demo/Default")[0] + "/DevCDRAgentCore.msi";
            else
                ViewBag.MSI = "https://devcdrcore.azurewebsites.net/DevCDRAgentCore.msi";
            ViewBag.Route = "/ro/chat";

            var sRoot = Directory.GetCurrentDirectory();
            if (System.IO.File.Exists(Path.Combine(sRoot, "wwwroot/plugin_ContextMenu.cshtml")))
            {
                ViewBag.Menu = System.IO.File.ReadAllText(Path.Combine(sRoot, "wwwroot/plugin_ContextMenu.cshtml"));
                ViewBag.ExtMenu = true;
            }
            return View();
        }

        [AllowAnonymous]
        public ActionResult GetData(string Instance)
        {
            JArray jData = new JArray();
            try
            {
                Type xType = Type.GetType("DevCDRServer." + Instance);

                MemberInfo[] memberInfos = xType.GetMember("jData", BindingFlags.Public | BindingFlags.Static);

                jData = ((FieldInfo)memberInfos[0]).GetValue(new JArray()) as JArray;
            }
            catch { }

            JObject oObj = new JObject
                    {
                        { "data", jData }
                    };

            return new ContentResult
            {
                Content = oObj.ToString(Newtonsoft.Json.Formatting.None),
                ContentType = "application/json"
                //ContentEncoding = Encoding.UTF8
            };
        }

        public int ClientCount(string Instance)
        {
            int iCount = 0;
            try
            {
                Type xType = Type.GetType("DevCDRServer." + Instance);

                MemberInfo[] memberInfos = xType.GetMember("ClientCount", BindingFlags.Public | BindingFlags.Static);

                var oCount = ((PropertyInfo)memberInfos[0]).GetValue(new int());

                if (oCount == null)
                    iCount = 0;
                else
                    iCount = (int)oCount;
            }
            catch { }

            return iCount;
        }

        [AllowAnonymous]
        public ActionResult Groups(string Instance)
        {
            List<string> lGroups = new List<string>();
            try
            {
                Type xType = Type.GetType("DevCDRServer." + Instance);

                MemberInfo[] memberInfos = xType.GetMember("lGroups", BindingFlags.Public | BindingFlags.Static);

                lGroups = ((FieldInfo)memberInfos[0]).GetValue(new List<string>()) as List<string>;
            }
            catch { }

            lGroups.Remove("web");
            lGroups.Remove("Devices");

            return new ContentResult
            {
                Content = JsonConvert.SerializeObject(lGroups, Formatting.None),
                ContentType = "application/json"
                //ContentEncoding = Encoding.UTF8
            };
        }

        //[AllowAnonymous]
        //public ActionResult GetRZCatalog(string Instance)
        //{
        //    List<string> lRZCat = new List<string>();
        //    try
        //    {
        //        string sCat = SWResults("");
        //        JArray oCat = JArray.Parse(sCat);
        //        lRZCat = JArray.Parse(sCat).SelectTokens("..ShortName").Values<string>().OrderBy(t => t).ToList();
        //    }
        //    catch { }

        //    return new ContentResult
        //    {
        //        Content = JsonConvert.SerializeObject(lRZCat, Formatting.None),
        //        ContentType = "application/json"
        //        //ContentEncoding = Encoding.UTF8
        //    };
        //}

        internal ActionResult SetResult(string Instance, string Hostname, string Result)
        {
            JArray jData = new JArray();
            try
            {
                Type xType = Type.GetType("DevCDRServer." + Instance);

                MemberInfo[] memberInfos = xType.GetMember("jData", BindingFlags.Public | BindingFlags.Static);

                jData = ((FieldInfo)memberInfos[0]).GetValue(new JArray()) as JArray;

                var tok = jData.SelectToken("[?(@.Hostname == '" + Hostname + "')].ScriptResult");
                tok = Result;
                jData.SelectToken("[?(@.Hostname == '" + Hostname + "')].ScriptResult").Replace(tok);

                ((FieldInfo)memberInfos[0]).SetValue(new JArray(), jData);

                //AzureLog.PostAsync(new { Computer = Hostname, EventID = 3000, Description = $"Result: {Result}" });
            }
            catch { }


            return new ContentResult();
        }

        internal string GetID(string Instance, string Host)
        {
            string sID = "";
            try
            {
                Type xType = Type.GetType("DevCDRServer." + Instance);

                MethodInfo methodInfo = xType.GetMethod(
                                            "GetID",
                                            BindingFlags.Public | BindingFlags.Static
                                        );
                sID = methodInfo.Invoke(new object(), new object[] { Host }) as string;
            }
            catch { }

            return sID;
        }

        internal void Reload(string Instance)
        {
            string sID = "";
            try
            {
                AzureLog.PostAsync(new { Computer = Environment.MachineName, EventID = 1001, Description = $"Reloading {Instance}" });

                _hubContext.Clients.All.SendAsync("init", "init");
                _hubContext.Clients.Group("web").SendAsync("newData", "Hub", ""); //Enforce PageUpdate

                Type xType = Type.GetType("DevCDRServer." + Instance);

                MethodInfo methodInfo = xType.GetMethod(
                                            "Clear",
                                            BindingFlags.Public | BindingFlags.Static
                                        );
                sID = methodInfo.Invoke(new object(), new object[] { Instance }) as string;
            }
            catch { }
        }

        [AllowAnonymous]
        [HttpPost]
        public object Command()
        {
            string sParams = "";
            using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8))
                sParams = reader.ReadToEnd();
            JObject oParams = JObject.Parse(sParams);

            string sCommand = oParams.SelectToken(@"$.command").Value<string>(); //get command name
            string sInstance = oParams.SelectToken(@"$.instance").Value<string>(); //get instance name
            string sArgs = oParams.SelectToken(@"$.args").Value<string>(); //get parameters

            if (string.IsNullOrEmpty(sInstance)) //Skip if instance is null
                return new ContentResult();

            List<string> lHostnames = new List<string>();
            foreach (var oRow in oParams["rows"])
            {
                try
                {
                    lHostnames.Add(oRow.Value<string>("Hostname"));
                }
                catch { }
            }

            switch (sCommand)
            {
                //case "AgentVersion":
                //    AgentVersion(lHostnames, sInstance);
                //    break;
                //case "Inv":
                //    string sEndPoint = Request.GetEncodedUrl().ToLower().Split("/devcdr/")[0];
                //    RunCommand(lHostnames, "Invoke-RestMethod -Uri '" + sEndPoint + "/jaindb/getps' | IEX;'Inventory complete..'", sInstance, sCommand);
                //    break;
                //case "Restart":
                //    RunCommand(lHostnames, "restart-computer -force", sInstance, sCommand);
                //    break;
                //case "Shutdown":
                //    RunCommand(lHostnames, "stop-computer -force", sInstance, sCommand);
                //    break;
                //case "Logoff":
                //    RunCommand(lHostnames, "(gwmi win32_operatingsystem).Win32Shutdown(4);'Logoff enforced..'", sInstance, sCommand);
                //    break;
                //case "Init":
                //    Reload(sInstance);
                //    break;
                //case "GetRZUpdates":
                //    RZScan(lHostnames, sInstance);
                //    break;
                //case "InstallRZUpdates":
                //    RZUpdate(lHostnames, sInstance, sArgs);
                //    break;
                //case "InstallRZSW":
                //    InstallRZSW(lHostnames, sInstance, sArgs);
                //    break;
                //case "GetGroups":
                //    GetGroups(lHostnames, sInstance);
                //    break;
                //case "SetGroups":
                //    SetGroups(lHostnames, sInstance, sArgs);
                //    break;
                //case "GetUpdates":
                //    RunCommand(lHostnames, "(Get-WUList -MicrosoftUpdate) | select Title | ConvertTo-Json", sInstance, sCommand);
                //    break;
                //case "InstallUpdates":
                //    RunCommand(lHostnames, "Install-WindowsUpdate -MicrosoftUpdate -IgnoreReboot -AcceptAll -Install;installing Updates...", sInstance, sCommand);
                //    break;
                //case "RestartAgent":
                //    RestartAgent(lHostnames, sInstance);
                //    break;
                //case "SetInstance":
                //    SetInstance(lHostnames, sInstance, sArgs);
                //    break;
                //case "SetEndpoint":
                //    SetEndpoint(lHostnames, sInstance, sArgs);
                //    break;
                //case "DevCDRUser":
                //    runUserCmd(lHostnames, sInstance, "", "");
                //    break;
                //case "WOL":
                //    sendWOL(lHostnames, sInstance, GetAllMACAddresses());
                //    break;
                //case "Compliance":
                //    string sEndPoint2 = Request.GetEncodedUrl().ToLower().Split("/devcdr/")[0];
                //    string complianceFile = Environment.GetEnvironmentVariable("ScriptCompliance") ?? "compliance_default.ps1";
                //    RunCommand(lHostnames, "Invoke-RestMethod -Uri '" + sEndPoint2 + "/jaindb/getps?filename=" + complianceFile + "' | IEX;'Inventory complete..'", sInstance, sCommand);
                //    break;
            }

            return new ContentResult();
        }

//        internal List<string> GetAllMACAddresses()
//        {
//            List<string> lResult = new List<string>();
//            var tItems = new JainDBController(_env, _cache).Query("$select=@MAC");
//            JArray jMacs = tItems.Result;

//            foreach (var jTok in jMacs.SelectTokens("..@MAC"))
//            {
//                if (jTok.Type == JTokenType.String)
//                    lResult.Add(jTok.Value<string>());
//                if (jTok.Type == JTokenType.Array)
//                    lResult.AddRange(jTok.Values<string>().ToList());
//            }

//            return lResult;
//        }

//        internal void RunCommand(List<string> Hostnames, string sCommand, string sInstance, string CmdName)
//        {
//            //IHubContext hubContext = GlobalHost.ConnectionManager.GetHubContext(sInstance);

//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + CmdName); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "RunCommand"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    AzureLog.PostAsync(new { Computer = sHost, EventID = 2001, Description = $"PSCommand: {sCommand}" });
//                    _hubContext.Clients.Client(sID).SendAsync("returnPS", sCommand, "Host");
//                }
//            }
//        }

//        internal void GetGroups(List<string> Hostnames, string sInstance)
//        {
//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "get Groups"); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "GetGroups"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    _hubContext.Clients.Client(sID).SendAsync("getgroups", "Host");
//                }
//            }
//        }

//        internal void SetGroups(List<string> Hostnames, string sInstance, string Args)
//        {
//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "set Groups"); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "SetGroups"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    AzureLog.PostAsync(new { Computer = sHost, EventID = 2009, Description = $"Set Agent Groups: {Args}" });
//                    _hubContext.Clients.Client(sID).SendAsync("setgroups", Args);
//                }
//            }
//        }

//        internal void AgentVersion(List<string> Hostnames, string sInstance)
//        {
//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "Get AgentVersion"); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "AgentVersion"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    AzureLog.PostAsync(new { Computer = sHost, EventID = 2008, Description = $"get Agent version" });
//                    _hubContext.Clients.Client(sID).SendAsync("version", "HUB");
//                }
//            }
//        }

//        internal void SetInstance(List<string> Hostnames, string sInstance, string Args)
//        {
//            if (string.IsNullOrEmpty(Args))
//                return;

//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "set Instance"); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "SetInstance"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    _hubContext.Clients.Client(sID).SendAsync("setinstance", Args);
//                }
//            }
//        }

//        internal void SetEndpoint(List<string> Hostnames, string sInstance, string Args)
//        {
//            if (string.IsNullOrEmpty(Args))
//                return;

//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "set Endpoint"); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "SetEndpoint"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    AzureLog.PostAsync(new { Computer = sHost, EventID = 2007, Description = $"set new Endpoint: {Args}" });
//                    _hubContext.Clients.Client(sID).SendAsync("setendpoint", Args);
//                }
//            }
//        }

//        internal void RestartAgent(List<string> Hostnames, string sInstance)
//        {
//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "restart Agent"); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "RestartAgent"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    AzureLog.PostAsync(new { Computer = sHost, EventID = 2006, Description = $"restart Agent" });
//                    _hubContext.Clients.Client(sID).SendAsync("restartservice", "HUB");
//                }
//            }
//        }

//        internal void InstallRZSW(List<string> Hostnames, string sInstance, string Args)
//        {
//            if (string.IsNullOrEmpty(Args))
//                return;

//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "Install SW"); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "InstallRZSW"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    AzureLog.PostAsync(new { Computer = sHost, EventID = 2005, Description = $"install RuckZuck software: {Args}" });
//                    _hubContext.Clients.Client(sID).SendAsync("rzinstall", Args);
//                }
//            }
//        }

//        internal void RZScan(List<string> Hostnames, string sInstance)
//        {
//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "get RZ Updates"); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "RZScan"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    AzureLog.PostAsync(new { Computer = sHost, EventID = 2003, Description = $"trigger RuckZuck scan" });
//                    _hubContext.Clients.Client(sID).SendAsync("rzscan", "HUB");
//                }
//            }
//        }

//        internal void RZUpdate(List<string> Hostnames, string sInstance, string Args = "")
//        {
//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "install RZ Updates"); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "RZUpdate"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    AzureLog.PostAsync(new { Computer = sHost, EventID = 2004, Description = $"trigger RuckZuck update" });
//                    _hubContext.Clients.Client(sID).SendAsync("rzupdate", Args);
//                }
//            }
//        }

//        internal void runUserCmd(List<string> Hostnames, string sInstance, string cmd, string args)
//        {
//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "run command as User..."); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "runUserCmd"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    AzureLog.PostAsync(new { Computer = sHost, EventID = 2010, Description = $"Run USer Processs: {cmd} {args}" });
//                    _hubContext.Clients.Client(sID).SendAsync("userprocess", cmd, args);
//                }
//            }
//        }

//        internal void sendWOL(List<string> Hostnames, string sInstance, List<string> MAC)
//        {
//            foreach (string sHost in Hostnames)
//            {
//                SetResult(sInstance, sHost, "triggered:" + "WakeUp devices..."); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "sendWOL"); //Enforce PageUpdate

//            foreach (string sHost in Hostnames)
//            {
//                if (string.IsNullOrEmpty(sHost))
//                    continue;

//                //Get ConnectionID from HostName
//                string sID = GetID(sInstance, sHost);

//                if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                {
//                    AzureLog.PostAsync(new { Computer = sHost, EventID = 2002, Description = $"WakeUp all devices" });
//                    _hubContext.Clients.Client(sID).SendAsync("wol", string.Join(';', MAC));
//                }
//            }
//        }

//#if DEBUG
//        [AllowAnonymous]
//#endif
//        [Authorize]
//        [HttpPost]
//        public object RunPS()
//        {
//            string sParams = "";
//            //Load response
//            using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8))
//                sParams = reader.ReadToEnd();

//            if (string.IsNullOrEmpty(sParams))
//                return new ContentResult(); ;

//            //Parse response as JSON
//            JObject oParams = JObject.Parse(sParams);

//            string sCommand = System.Uri.UnescapeDataString(oParams.SelectToken(@"$.psscript").Value<string>()); //get command
//            string sInstance = oParams.SelectToken(@"$.instance").Value<string>(); //get instance name
//            string sTitle = oParams.SelectToken(@"$.title").Value<string>(); //get title

//            if (string.IsNullOrEmpty(sInstance)) //Skip if instance is null
//                return new ContentResult();

//            List<string> lHostnames = new List<string>();

//            foreach (var oRow in oParams["rows"])
//            {
//                string sHost = oRow.Value<string>("Hostname");
//                SetResult(sInstance, sHost, "triggered:" + sTitle); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "RunPS"); //Enforce PageUpdate

//            foreach (var oRow in oParams["rows"])
//            {
//                try
//                {
//                    //Get Hostname from Row
//                    string sHost = oRow.Value<string>("Hostname");

//                    if (string.IsNullOrEmpty(sHost))
//                        continue;

//                    //Get ConnectionID from HostName
//                    string sID = GetID(sInstance, sHost);

//                    if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                    {
//                        AzureLog.PostAsync(new { Computer = sHost, EventID = 2050, Description = $"Run PS: {sCommand}" });
//                        _hubContext.Clients.Client(sID).SendAsync("returnPS", sCommand, "Host");
//                    }
//                }
//                catch { }
//            }

//            return new ContentResult();
//        }

//#if DEBUG
//        [AllowAnonymous]
//#endif
//        [Authorize]
//        [HttpPost]
//        public object RunUserPS()
//        {
//            string sParams = "";
//            //Load response
//            using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8))
//                sParams = reader.ReadToEnd();

//            if (string.IsNullOrEmpty(sParams))
//                return new ContentResult(); ;

//            //Parse response as JSON
//            JObject oParams = JObject.Parse(sParams);

//            string sCommand = System.Uri.UnescapeDataString(oParams.SelectToken(@"$.psscript").Value<string>()); //get command
//            string sInstance = oParams.SelectToken(@"$.instance").Value<string>(); //get instance name
//            string sTitle = oParams.SelectToken(@"$.title").Value<string>(); //get title

//            if (string.IsNullOrEmpty(sInstance)) //Skip if instance is null
//                return new ContentResult();

//            List<string> lHostnames = new List<string>();

//            foreach (var oRow in oParams["rows"])
//            {
//                string sHost = oRow.Value<string>("Hostname");
//                SetResult(sInstance, sHost, "triggered:" + sTitle); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "RunUserPS"); //Enforce PageUpdate

//            foreach (var oRow in oParams["rows"])
//            {
//                try
//                {
//                    //Get Hostname from Row
//                    string sHost = oRow.Value<string>("Hostname");

//                    if (string.IsNullOrEmpty(sHost))
//                        continue;

//                    //Get ConnectionID from HostName
//                    string sID = GetID(sInstance, sHost);

//                    if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                    {
//                        _hubContext.Clients.Client(sID).SendAsync("userprocess", "powershell.exe", "-command \"& { " + sCommand + " }\"");
//                        //hubContext.Clients.Client(sID).returnPS(sCommand, "Host");
//                    }
//                }
//                catch { }
//            }

//            return new ContentResult();
//        }

//#if DEBUG
//        [AllowAnonymous]
//#endif
//        [Authorize]
//        [HttpPost]
//        public object RunPSAsync()
//        {
//            string sParams = "";
//            //Load response
//            using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8))
//                sParams = reader.ReadToEnd();

//            if (string.IsNullOrEmpty(sParams))
//                return new ContentResult(); ;

//            //Parse response as JSON
//            JObject oParams = JObject.Parse(sParams);

//            string sCommand = System.Uri.UnescapeDataString(oParams.SelectToken(@"$.psscript").Value<string>()); //get command
//            string sInstance = oParams.SelectToken(@"$.instance").Value<string>(); //get instance name
//            string sTitle = oParams.SelectToken(@"$.title").Value<string>(); //get title

//            if (string.IsNullOrEmpty(sInstance)) //Skip if instance is null
//                return new ContentResult();

//            List<string> lHostnames = new List<string>();

//            foreach (var oRow in oParams["rows"])
//            {
//                string sHost = oRow.Value<string>("Hostname");
//                SetResult(sInstance, sHost, "triggered:" + sTitle); //Update Status
//            }
//            _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "RunPSAsync"); //Enforce PageUpdate

//            foreach (var oRow in oParams["rows"])
//            {
//                try
//                {
//                    //Get Hostname from Row
//                    string sHost = oRow.Value<string>("Hostname");

//                    if (string.IsNullOrEmpty(sHost))
//                        continue;

//                    //Get ConnectionID from HostName
//                    string sID = GetID(sInstance, sHost);

//                    if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                    {
//                        AzureLog.PostAsync(new { Computer = sHost, EventID = 2051, Description = $"Run PSAsync: {sCommand}" });
//                        _hubContext.Clients.Client(sID).SendAsync("returnPSAsync", sCommand, "Host");
//                    }
//                }
//                catch { }
//            }

//            return new ContentResult();
//        }

//#if DEBUG
//        [AllowAnonymous]
//#endif
//        [Authorize]
//        [HttpPost]
//        public object RunPSFile()
//        {
//            string sParams = "";
//            //Load response
//            using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8))
//                sParams = reader.ReadToEnd();

//            if (string.IsNullOrEmpty(sParams))
//                return new ContentResult(); ;

//            //Parse response as JSON
//            JObject oParams = JObject.Parse(sParams);

//            string sFile = System.Uri.UnescapeDataString(oParams.SelectToken(@"$.file").Value<string>()); //get command
//            string sInstance = oParams.SelectToken(@"$.instance").Value<string>(); //get instance name
//            string sTitle = oParams.SelectToken(@"$.title").Value<string>(); //get title

//            string sFilePath = Path.Combine(_env.WebRootPath, sFile);

//            if (System.IO.File.Exists(sFilePath))
//            {
//                if (!sFilePath.StartsWith(Path.Combine(_env.WebRootPath, "PSScripts")))
//                    return new ContentResult();

//                string sCommand = System.IO.File.ReadAllText(sFilePath);

//                if (string.IsNullOrEmpty(sInstance)) //Skip if instance is null
//                    return new ContentResult();

//                List<string> lHostnames = new List<string>();

//                foreach (var oRow in oParams["rows"])
//                {
//                    string sHost = oRow.Value<string>("Hostname");
//                    SetResult(sInstance, sHost, "triggered:" + sTitle); //Update Status
//                }
//                _hubContext.Clients.Group("web").SendAsync("newData", "HUB", "RunPSFile"); //Enforce PageUpdate

//                foreach (var oRow in oParams["rows"])
//                {
//                    try
//                    {
//                        //Get Hostname from Row
//                        string sHost = oRow.Value<string>("Hostname");

//                        if (string.IsNullOrEmpty(sHost))
//                            continue;

//                        //Get ConnectionID from HostName
//                        string sID = GetID(sInstance, sHost);

//                        if (!string.IsNullOrEmpty(sID)) //Do we have a ConnectionID ?!
//                        {
//                            AzureLog.PostAsync(new { Computer = sHost, EventID = 2052, Description = $"Run PSFile: {sFile}" });
//                            _hubContext.Clients.Client(sID).SendAsync("returnPSAsync", sCommand, "Host");
//                        }
//                    }
//                    catch { }
//                }
//            }

//            return new ContentResult();
//        }

//        internal string SWResults(string Searchstring)
//        {
//            string sCatFile = @"/App_Data/rzcat.json";
//            string sResult = "";
//            string sURL = "https://ruckzuck.tools";

//            sCatFile = Path.Combine(_env.WebRootPath, "rzcat.json");

//            try
//            {
//                if (string.IsNullOrEmpty(Searchstring))
//                {
//                    if (System.IO.File.Exists(sCatFile))
//                    {
//                        if (DateTime.Now.ToUniversalTime() - System.IO.File.GetCreationTime(sCatFile).ToUniversalTime() <= new TimeSpan(1, 0, 1))
//                        {
//                            sResult = System.IO.File.ReadAllText(sCatFile);
//                            if (sResult.StartsWith("[") & sResult.Length > 64) //check if it's JSON
//                            {
//                                return sResult;
//                            }
//                        }
//                    }
//                }
//                else
//                {

//                }

//                HttpClient oClient = new HttpClient();
//                oClient.DefaultRequestHeaders.Accept.Clear();
//                oClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
//                var response = oClient.GetStringAsync(sURL + "/rest/v2/getcatalog");
//                response.Wait(10000); //10s max.
//                if (response.IsCompleted)
//                {
//                    sResult = response.Result;
//                    if (sResult.StartsWith("[") & sResult.Length > 64)
//                    {
//                        if (string.IsNullOrEmpty(Searchstring))
//                            System.IO.File.WriteAllText(sCatFile, sResult);

//                        return sResult;
//                    }
//                }


//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine(ex.Message);
//            }

//            //return old File
//            if (System.IO.File.Exists(sCatFile))
//            {
//                return System.IO.File.ReadAllText(sCatFile);
//            }

//            return "";
//        }

    }
}