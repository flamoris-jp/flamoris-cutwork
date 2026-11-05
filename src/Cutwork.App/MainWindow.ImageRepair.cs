using System.Windows;
using System.Windows.Input;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.App.Generation;

namespace Flamoris.Cutwork.App;

public partial class MainWindow
{
    private bool _imageRepairOpen;
    private static readonly RoutedCommand ImageRepairCommand=new(nameof(ImageRepairCommand),typeof(MainWindow));
    private void InitializeImageRepair()
    {
        ImageRepairMenuItem.Command=ImageRepairCommand;
        CommandBindings.Add(new CommandBinding(ImageRepairCommand,(_,_)=>OpenImageRepair(),(_,e)=>
            e.CanExecute=!_remoteEditing && !_imageRepairOpen && !HumanBusy()
                && _session.SelectedLayerId is {} id && _session.Document?.GetLayer(id) is PartLayer));
    }
    private void OpenImageRepair()
    {
        if(_session.SelectedLayerId is not {} id)return;
        try
        {
            var request=new GeneratedRepair(_session,id);_imageRepairOpen=true;
            new ImageRepairWindow(_session,request){Owner=this}.ShowDialog();
        }
        catch(Exception)
        {
            MessageBox.Show(this,LocalizationService.Current["AIRepair_CannotStart"],
                LocalizationService.Current["AIRepair_Title"],MessageBoxButton.OK,MessageBoxImage.Information);
        }
        finally{_imageRepairOpen=false;CommandManager.InvalidateRequerySuggested();}
    }
}
