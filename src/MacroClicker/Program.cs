namespace MacroClicker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.ThreadException += (s, e) =>
        {
            try
            {
                MessageBox.Show("程序发生未处理异常：\n" + e.Exception.Message + "\n\n" + e.Exception.StackTrace,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        };
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
