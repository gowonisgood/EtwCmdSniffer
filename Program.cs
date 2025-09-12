using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

class Program
{
    // 첫 번째 코드에서 가져온 모든 Regex 패턴들
    static readonly Regex BashDashCRegex =
        new(@"(?:^|\s)-[A-Za-z]*c\s+(['""])(?<cmd>.+?)\1",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    static readonly Regex SnapshotRegex =
        new(@"[\\/]\.claude[\\/].*?[\\/]shell-snapshots[\\/]snapshot-bash-",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

                    // --- 로직 통합 시작 ---
                    string imageName = data.ImageFileName ?? string.Empty;
                    string cmdline = data.CommandLine ?? string.Empty;

                    // 자식 프로세스가 bash.exe일 경우, 상세 파싱 로직 실행
                    if (imageName.EndsWith("bash.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        // 1) -c / -lc / -ic 뒤에 전달된 실제 명령 파싱
                        var mDashC = BashDashCRegex.Match(cmdline);
                        if (mDashC.Success)
                        {
                            string userCmd = mDashC.Groups["cmd"].Value;
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"  -> [BASH CMD] PID: {data.ProcessID}, CMD: {userCmd}");
                            Console.ResetColor();
                        }
                        // 2) 스냅샷 경유 실행 시 힌트 출력 + eval 패턴이면 추가 파싱
                        else if (SnapshotRegex.IsMatch(cmdline))
                        {
                            var mEval = EvalCmdRegex.Match(cmdline);
                            if (mEval.Success)
                            {
                                string userCmd = mEval.Groups["cmd"].Value;
                                Console.ForegroundColor = ConsoleColor.Cyan;
                                Console.WriteLine($"  -> [BASH SNAPSHOT/EVAL] PID: {data.ProcessID}, CMD: {userCmd}");
                                Console.ResetColor();
                            }
                            else
                            {
                                Console.ForegroundColor = ConsoleColor.Yellow;
                                Console.WriteLine($"  -> [BASH SNAPSHOT 감지] PID: {data.ProcessID}, CMDLINE: {cmdline}");
                                Console.ResetColor();
                            }
                        }
                        // 3) 위 두 경우가 아니면 최소한 실행 사실은 기록
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.WriteLine($"  -> [BASH 실행] PID: {data.ProcessID}, CMDLINE: {cmdline}");
                            Console.ResetColor();
                        }
                    }
                    // --- 로직 통합 끝 ---
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