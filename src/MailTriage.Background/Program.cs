// mail-triage-background.exe: no window, ever. What the logon task runs (`watch`); anything else runs as the
// console program would, with nothing shown.
return MailTriage.Cli.Run(args, new MailTriage.CliHost { Out = TextWriter.Null, Err = TextWriter.Null, Echo = false });
