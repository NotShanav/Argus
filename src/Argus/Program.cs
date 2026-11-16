using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PresenceGuard
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            using var mutex = new Mutex(true, @"Local\Argus.Single", out bool createdNew);
            if (!createdNew) return; // already running: exit quietly

            var log = new ActivityLog();
            var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };

            app.DispatcherUnhandledException += (_, e) =>
            {
                log.Add(LogKind.Error, "UI error: " + e.Exception.Message);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                log.Add(LogKind.Error, "Unhandled: " + (e.ExceptionObject as Exception)?.Message);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                log.Add(LogKind.Error, "Task error: " + e.Exception.GetBaseException().Message);
                e.SetObserved();
            };

            TrayHost? tray = null;
            IPresenceSensor? presence = null;
            GuardController? guard = null;

            Action exitApp = () =>
            {
                try { guard?.Stop(); } catch { }
                try { tray?.Dispose(); } catch { }
                try { presence?.Dispose(); } catch { }
                try { app.Shutdown(); } catch { }
            };

            try
            {
                IrCamera.Diag = m => log.Add(LogKind.Info, m);
                FaceEngine.Diag = m => log.Add(LogKind.Info, m);
                var cfg = new JsonConfigStore();
                var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
                IIrCamera cam = new IrCamera();
                IFaceEngine engine = new FaceEngine(modelsDir);
                IEnrollmentStore store = new DpapiEnrollmentStore();
                IEnrollmentService enroll = new EnrollmentService(cam, engine, store);
                presence = new PresenceSensor();
                ILockAnimation anim = new SealOverlay();

                guard = new GuardController(cam, engine, store, presence, anim, cfg, log);
                tray = new TrayHost(guard, cfg, log, enroll, store, exitApp);

                log.Add(LogKind.Info, "Argus started");
                tray.Show();
                guard.Start();
            }
            catch (Exception ex)
            {
                log.Add(LogKind.Error, "Startup failed: " + ex.Message);
                exitApp();
                return;
            }

            app.Run();
            try { mutex.ReleaseMutex(); } catch { }
        }
    }
}
