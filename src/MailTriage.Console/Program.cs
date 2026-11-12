// mail-triage.exe: every command, with output. Subjects carry emoji, so the console gets UTF-8.
try
{
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
}
catch (IOException)
{
    // no console: output is redirected
}
return MailTriage.Cli.Run(args, new MailTriage.CliHost { Echo = true });
