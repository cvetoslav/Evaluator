using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

using OJS.Workers.Executors.Process;
using OJS.Workers.Executors.JobObjects;

using System.Web.Script;
using System.Web.Script.Serialization;

using System.Timers;
using System.Net;
using System.Net.Http;

namespace evaluator
{
    class Test
    {
        public string Status;
        public bool Tested;
        public double Time;
        public long Memory;
        public string Result;
        public double Points;
    }

    class TestInfo
    {
        public string url;
        public string inp_name, out_name;
    }

    class Query
    {
        public int id, tl, ml;
        public string source, checker_source, update_url;
        public List<TestInfo> t_info;
    }

    class Program
    {
        private static List<Test> tests;
        private static List<TestInfo> tests_info;

        private const string AUTH_USER = "USER", AUTH_PASS = "PASS";

        private static string CWD, update_url, ce_message;

        private static double overal_points;

        private static bool upd_flag;

        private static int sub_id;

        public static string result; public static int exitcode; public static long memoryused; public static TimeSpan time; public static TimeSpan processortime, userprocessortime;
        public static string output, erroroutput;

        public static void Execute(string fileName, string inputData, int timeLimit, int memoryLimit, IEnumerable<string> executionArguments = null)
        {
            result = "ok";
            var workingDirectory = new FileInfo(fileName).DirectoryName;

            using (var restrictedProcess = new RestrictedProcess(fileName, workingDirectory, executionArguments, Math.Max(4096, (inputData.Length * 2) + 4)))
            {
                restrictedProcess.StandardInput.WriteLineAsync(inputData).ContinueWith(
                    delegate
                    {
                        if (!restrictedProcess.IsDisposed)
                        {
                            restrictedProcess.StandardInput.FlushAsync().ContinueWith(
                                delegate
                                {
                                    restrictedProcess.StandardInput.Close();
                                });
                        }
                    });

                var processOutputTask = restrictedProcess.StandardOutput.ReadToEndAsync().ContinueWith(
                    x =>
                    {
                        output = x.Result;
                    });

                var errorOutputTask = restrictedProcess.StandardError.ReadToEndAsync().ContinueWith(
                    x =>
                    {
                        erroroutput = x.Result;
                    });

                const int TimeIntervalBetweenTwoMemoryConsumptionRequests = 45;
                var memoryTaskCancellationToken = new CancellationTokenSource();
                var memoryTask = Task.Run(
                    () =>
                    {
                        while (true)
                        {
                            var peakWorkingSetSize = restrictedProcess.PeakWorkingSetSize;

                            memoryused = Math.Max(memoryused, peakWorkingSetSize);

                            if (memoryTaskCancellationToken.IsCancellationRequested)
                            {
                                return;
                            }

                            Thread.Sleep(TimeIntervalBetweenTwoMemoryConsumptionRequests);
                        }
                    },
                    memoryTaskCancellationToken.Token);

                restrictedProcess.Start(timeLimit, memoryLimit);

                var exited = restrictedProcess.WaitForExit((int)(timeLimit * 1.5));
                if (!exited)
                {
                    restrictedProcess.Kill();
                    result = "tl";
                }

                memoryTaskCancellationToken.Cancel();
                try
                {
                    memoryTask.Wait(TimeIntervalBetweenTwoMemoryConsumptionRequests);
                }
                catch (AggregateException ex)
                {
                }

                try
                {
                    errorOutputTask.Wait(100);
                }
                catch (AggregateException ex)
                {
                }

                try
                {
                    processOutputTask.Wait(100);
                }
                catch (AggregateException ex)
                {
                }

                Debug.Assert(restrictedProcess.HasExited, "Restricted process didn't exit!");

                exitcode = restrictedProcess.ExitCode;
                time = restrictedProcess.ExitTime - restrictedProcess.StartTime;
                processortime = restrictedProcess.PrivilegedProcessorTime;
                userprocessortime = restrictedProcess.UserProcessorTime;
            }

            if ((processortime + userprocessortime).TotalMilliseconds > timeLimit)
            {
                result = "tl";
            }

            if (!string.IsNullOrEmpty(erroroutput))
            {
                result = "re";
            }

            if (memoryused > memoryLimit)
            {
                result = "ml";
            }

        }

        private static string Compile(string source_path, string exe_path, int compilation_tl)
        {
            if (File.Exists(exe_path)) File.Delete(exe_path);
            ProcessStartInfo si = new ProcessStartInfo();
            si.FileName = "g++.exe";
            si.UseShellExecute = false;
            si.Arguments = "\""+source_path+"\" -o \""+exe_path+"\"";
            si.WindowStyle = ProcessWindowStyle.Hidden;
            si.CreateNoWindow = true;
            si.RedirectStandardError = true;
            Process p = new Process();
            p.StartInfo = si;
            p.Start();
            p.WaitForExit(compilation_tl);
            if (!p.HasExited) p.Kill();
            if (!File.Exists(exe_path)) return p.StandardError.ReadToEnd();
            return "ok";
        }

        private static void DownloadFile(string url, string path)
        {
            using (var client = new WebClient())
            {
                client.DownloadFile(url, path);
            }
        }

        public static string enc(string s)
        {
            StringBuilder sb = new StringBuilder(s);
            for (int i=0; i<sb.Length; i++)
            {
                if (sb[i] == '\"') sb.Insert(i, '\\');
            }
            return sb.ToString();
        }

        public static bool IsFileReady(String sFilename)
        {
            try
            {
                using (FileStream inputStream = File.Open(sFilename, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (inputStream.Length > 0) return true;
                    else return false;
                }
            }
            catch (Exception) {return false;}
        }

        private static bool Checker_Init(string checker_src)
        {
            if (Directory.Exists(CWD + "\\checker")) Directory.Delete(CWD + "\\checker", true);
            Directory.CreateDirectory(CWD + "\\checker");
            //while (!IsFileReady(CWD + "\\checker\\checker.cpp")) ;
            File.WriteAllText(CWD + "\\checker\\checker.cpp", checker_src);
            return Compile(CWD + "\\checker\\checker.cpp", CWD + "\\checker\\checker.exe", 10000) == "ok";
        }

        private static bool Solution_Init(string solution_src)
        {
            if (Directory.Exists(CWD + "\\sandbox")) Directory.Delete(CWD + "\\sandbox", true);
            Directory.CreateDirectory(CWD + "\\sandbox");
           // while (!IsFileReady(CWD + "\\sandbox\\sandbox.cpp")) ;
            File.WriteAllText(CWD + "\\sandbox\\solution.cpp", solution_src);
            string ret = Compile(CWD + "\\sandbox\\solution.cpp", CWD + "\\sandbox\\solution.exe", 10000);
            if (ret == "ok") return true;
            ce_message = ret;
            return false;
        }

        private static bool Tests_Init()
        {
            try
            {

                if (Directory.Exists(CWD + "\\tests")) Directory.Delete(CWD + "\\tests", true);
                Directory.CreateDirectory(CWD + "\\tests");
                /*foreach (TestInfo ti in tests_info)
                {
                    DownloadFile(ti.url + ti.inp_name, CWD + "\\tests\\" + ti.inp_name);
                    DownloadFile(ti.url + ti.out_name, CWD + "\\tests\\" + ti.out_name);
                }*/
                foreach (TestInfo ti in tests_info)
                {
                    File.Copy("D:\\xampp\\htdocs\\tests\\" + ti.url + ti.inp_name, CWD + "\\tests\\" + ti.inp_name);
                    File.Copy("D:\\xampp\\htdocs\\tests\\" + ti.url + ti.out_name, CWD + "\\tests\\" + ti.out_name);
                }

                return true;
            }catch(Exception e) { return false; }
        }

        private static async void Update()
        {
            string data = new JavaScriptSerializer().Serialize(tests);

            var post_data = new Dictionary<string, string>();
            post_data.Add("data", data);
            post_data.Add("sub_id", sub_id.ToString());
            post_data.Add("overal_points", overal_points.ToString());
            //Console.WriteLine("Preparing to update...");
            var content = new FormUrlEncodedContent(post_data);
            using (var client = new HttpClient())
            {
                try
                {
                    var byteArray = Encoding.ASCII.GetBytes(AUTH_USER+":"+AUTH_PASS);
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
                    //Console.WriteLine("Updating... " + data);
                    HttpResponseMessage asd = await client.PostAsync(update_url, content);

                }
                catch (Exception e) { Console.WriteLine("Unable to update!"); }
            }

        }

        private static void upd(Object sender, ElapsedEventArgs e)
        {
            if(upd_flag)
            {
                upd_flag = false;
                Update();
            }
        }

        private static void Tests_Assign()
        {
            tests = new List<Test>();
            foreach(TestInfo ti in tests_info)
            {
                Test t = new Test();
                t.Memory = 0;
                t.Time = 0.0;
                t.Points = 0.0;
                t.Status = "Pending";
                t.Tested = false;
                tests.Add(t);
            }
        }

        private static void SetTestsStatus(string status)
        {
            foreach (Test t in tests) t.Status = status;
        }
        private static void SetTestsTested(bool f)
        {
            foreach (Test t in tests) t.Tested = f;
        }
        private static void SetTestsTime(int time)
        {
            foreach (Test t in tests) t.Time = time;
        }
        private static void SetTestsMemory(int mem)
        {
            foreach (Test t in tests) t.Memory = mem;
        }
        private static void SetTestsPoints(double p)
        {
            foreach (Test t in tests) t.Points = p;
        }
        private static void SetTestsResult(string s)
        {
            foreach (Test t in tests) t.Result = s;
        }

        static void Main(string[] args)
        {
            if (args.Length < 1) return;

            CWD = Directory.GetCurrentDirectory();

            File.WriteAllText(CWD + "\\last_log.txt", args[0]);

            var data = args[0];

            var query = new JavaScriptSerializer().Deserialize<Query>(data);

            sub_id = query.id;
            int tl = query.tl;
            int ml = query.ml;
            string src = query.source;
            string checker_src = query.checker_source;
            update_url = query.update_url;
            tests_info = query.t_info;

            Tests_Assign();

            SetTestsStatus("Preparing");
            SetTestsTested(false);
            SetTestsPoints(0.0);
            SetTestsTime(0);
            SetTestsMemory(0);

            upd_flag = false;
            System.Timers.Timer t = new System.Timers.Timer();
            t.Elapsed += new ElapsedEventHandler(upd);
            t.Interval = 200;
            t.Enabled = true;
            t.Start();

            Console.WriteLine("Compiling Checker...");
            if(checker_src != "" && !Checker_Init(checker_src))
            {
                t.Stop();
                SetTestsStatus("Internal Error");
                SetTestsResult("ie");
                SetTestsTested(true);
                Update();
                return;
            }

            Console.WriteLine("Compiling Solution...");
            if (!Solution_Init(src))
            {
                t.Stop();
                File.WriteAllText(CWD + "\\sol_ce.txt", ce_message);
                SetTestsStatus("Compilation Error");
                SetTestsResult("ce");
                SetTestsTested(true);
                Update(); Console.ReadKey();
                return;
            }

            Console.WriteLine("Downloading tests...");
            if (!Tests_Init())
            {
                t.Stop();
                SetTestsStatus("Internal Error");
                SetTestsResult("ie");
                SetTestsTested(true);
                Update();
                return;
            }

            Console.WriteLine("Evaluating Solution...");
            int i = 0; overal_points = 0.0;
            foreach(TestInfo ti in tests_info)
            {
                Test test = tests[i];
                test.Status = "Running"; upd_flag = true;
                string input = File.ReadAllText(CWD + "\\tests\\" + ti.inp_name);
                string sol = File.ReadAllText(CWD + "\\tests\\" + ti.out_name);
                Execute(CWD + "\\sandbox\\solution.exe", input, tl, ml);
                string res = result;

                test.Memory = memoryused;
                test.Time = time.TotalSeconds;
                test.Status = "Testing"; upd_flag = true;

                if (res == "ok")
                {
                    if (checker_src != "")
                    {
                        List<string> arguments = new List<string>();
                        arguments.Add("\"" + enc(output) + "\"");
                        arguments.Add("\"" + enc(input) + "\"");
                        arguments.Add("\"" + enc(sol) + "\"");
                        Execute(CWD + "\\checker\\checker.exe", "", 2000, 512000000, arguments);
                        try { test.Points = Convert.ToDouble(output); }
                        catch (Exception e) { test.Points = 0.0; res = "ie"; }

                        File.WriteAllText(CWD + "\\cum.txt", erroroutput);
                    }
                    else
                    {
                        if (output == sol)
                        {
                            test.Points = 1.0;
                        }
                        else { test.Points = 0.0; res = "wa"; }
                    }
                }

                test.Status = "Evaluated";
                test.Result = res;
                test.Tested = true; upd_flag = true;

                i++; overal_points += test.Points;

            }//t.Stop(); t.Enabled = false;

            overal_points *= 100.0;
            overal_points /= i;

            Console.WriteLine("This was the evaluator's work. Press any key to exit...");
            Thread.Sleep(200);
            Update();

            Console.WriteLine();
            //Console.ReadKey();
        }
    }
}
