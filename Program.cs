namespace AudioCombiner;

static class Program
{
    public const string ShowSignalName = "AudioCombiner_Show";

    [STAThread]
    static void Main(string[] args)
    {
        bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

        using var mutex = new Mutex(true, "AudioCombiner_SingleInstance", out bool firstInstance);
        if (!firstInstance && Has("--updated"))
        {
            // Після оновлення стара версія ще завершується — чекаємо, поки вона відпустить м'ютекс
            try { firstInstance = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { firstInstance = true; }
        }
        if (!firstInstance)
        {
            // Програма вже працює (можливо, у треї) — просимо її показати вікно
            try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch { }
            return;
        }

        Updater.Cleanup();

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "Audio Combiner — помилка", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new MainForm(startHidden: Has("--tray"), startRelay: Has("--relay")));
    }
}
