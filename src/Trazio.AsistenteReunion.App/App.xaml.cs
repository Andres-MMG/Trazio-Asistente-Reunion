using System.Windows;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;
    private readonly IDataRootLocator _dataRootLocator = new RegistryDataRootLocator();
    private StorageMigrationService? _storageMigration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceGuard = SingleInstanceGuard.TryAcquire();
        if (_instanceGuard is null)
        {
            MessageBox.Show(
                "Trazio Asistente Reunión ya está en ejecución para este usuario de Windows.",
                "Trazio Asistente Reunión",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _storageMigration = new StorageMigrationService(_dataRootLocator, new WindowsStoragePlatform());
        string dataRoot;
        try
        {
            dataRoot = _storageMigration.ResolveActiveRoot(ApplicationPaths.DefaultDataDirectory, AppContext.BaseDirectory);
            ApplicationPaths.ConfigureOnce(dataRoot);
        }
        catch (Exception ex)
        {
            StorageLocationState? storageState = null;
            try { storageState = _dataRootLocator.Load(); } catch { }
            var pending = storageState?.Pending;
            var committedCleanup = pending is not null && storageState?.CleanupPendingSource is not null;
            var instruction = pending is null
                ? "Vuelve a conectar la unidad local configurada o corrige la ubicación de almacenamiento y luego inicia Trazio nuevamente."
                : committedCleanup
                    ? "El traslado verificado ya fue confirmado. Resuelve el problema de limpieza informado e inicia Trazio nuevamente; este traslado ya no se puede cancelar."
                    : "Elige Sí para cancelar este traslado pendiente y conservar la ubicación actual, o No para volver a intentarlo en el próximo inicio.";
            var result = MessageBox.Show(
                $"Trazio no pudo preparar de forma segura su carpeta de datos. No se abrió ninguna base de datos de reuniones.\n\n{ex.Message}\n\n{instruction}",
                "Almacenamiento no disponible",
                pending is not null && !committedCleanup ? MessageBoxButton.YesNo : MessageBoxButton.OK,
                MessageBoxImage.Error);
            if (pending is not null && !committedCleanup && result == MessageBoxResult.Yes)
            {
                try { _storageMigration.CancelScheduled(pending); }
                catch (Exception cancelError)
                {
                    MessageBox.Show($"No se pudo cancelar de forma segura el traslado pendiente. No se abrió ningún dato.\n\n{cancelError.Message}",
                        "No se canceló el traslado de almacenamiento", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            Shutdown();
            return;
        }

        MainWindow = new MainWindow(_dataRootLocator, _storageMigration);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceGuard?.Dispose();
        _instanceGuard = null;
        base.OnExit(e);
    }
}

