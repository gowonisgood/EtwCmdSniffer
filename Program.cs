using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

class Program
{
    // eval '...' 또는 eval "..." 패턴을 추출하기 위한 Regex. 가장 핵심적인 역할을 합니다.
    static readonly Regex EvalCmdRegex =
        new(@"eval\s+(['""])(?<cmd>.+?)\1",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    static void Main(string[] args)
    {
        // 관리자 권한 확인
        if (TraceEventSession.IsElevated() != true)
        {
            Console.Error.WriteLine("이 프로그램은 관리자 권한으로 실행해야 합니다.");
            return;
        }

        // 인자 파싱
        if (args.Length == 0 || !int.TryParse(args[0], out int rootPid))
        {
            Console.WriteLine("사용법: program.exe <추적할 부모 프로세스 ID>");
            return;
        }

        Console.WriteLine($"루트 PID {rootPid}의 프로세스 트리 모니터링을 시작합니다. (중지하려면 Ctrl+C)");

        var processTreePids = new HashSet<int> { rootPid };

        using (var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName))
        {
            session.StopOnDispose = true;
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);

            var kernelParser = session.Source.Kernel;

            kernelParser.ProcessStart += (ProcessTraceData data) =>
            {
                // 우리 프로세스 트리에 속한 자식 프로세스인지 확인
                if (processTreePids.Contains(data.ParentID))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[자식 프로세스 시작] 부모 PID: {data.ParentID}, 새 PID: {data.ProcessID}, 이름: {data.ProcessName}");
                    Console.ResetColor();
                    
                    processTreePids.Add(data.ProcessID);

                    // --- 로직 수정 지점 ---
                    string imageName = data.ImageFileName ?? string.Empty;
                    string cmdline = data.CommandLine ?? string.Empty;

                    // 자식 프로세스가 bash.exe일 경우, eval 명령어 추출 시도
                    if (imageName.EndsWith("bash.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        // 'eval "..."' 패턴을 찾아 최우선으로 파싱합니다.
                        var mEval = EvalCmdRegex.Match(cmdline);
                        if (mEval.Success)
                        {
                            // eval 패턴이 발견되면, 그 안의 내용만 최종 명령어로 간주하고 출력합니다.
                            string finalCommand = mEval.Groups["cmd"].Value;

                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"  -> [EVAL CMD] PID: {data.ProcessID}, CMD: {finalCommand}");
                            Console.ResetColor();
                        }
                        // eval 패턴이 없으면 아무것도 출력하지 않아, 원하는 로그만 필터링합니다.
                    }
                }
            };

            kernelParser.ProcessStop += (ProcessTraceData data) =>
            {
                if (processTreePids.Contains(data.ProcessID))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[자식 프로세스 종료]  PID: {data.ProcessID}, 이름: {data.ProcessName}, 종료 코드: {data.ExitStatus}");
                    Console.ResetColor();
                    
                    processTreePids.Remove(data.ProcessID);
                }
            };

            session.Source.Process();
        }
    }
}