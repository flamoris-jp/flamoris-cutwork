using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.App.Generation;

public partial class ImageRepairWindow : Window
{
    private readonly EditorSession session;
    private readonly GeneratedRepair request;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string,Func<object?>> fields=[];
    private StudioRepairClient? client;
    private bool running, hasCandidate, closed;
    private int workWidth,workHeight;
    private static string T(string key)=>LocalizationService.Current["AIRepair_"+key];

    public ImageRepairWindow(EditorSession session,GeneratedRepair request)
    {
        InitializeComponent();this.session=session;this.request=request;
        Title=T("Title");Explanation.Text=T("Explanation");ServerLabel.Text=T("Server");EmailLabel.Text=T("Email");
        PasswordLabel.Text=T("Password");ConnectButton.Content=T("Connect");WorkflowLabel.Text=T("Workflow");
        ReferenceLabel.Text=T("Reference");ComparisonLabel.Text=T("Comparison");
        GenerateButton.Content=T("Generate");CancelJobButton.Content=T("CancelJob");ApplyButton.Content=T("Apply");CloseButton.Content=T("Close");
        Server.Text=Environment.GetEnvironmentVariable("CUTWORK_STUDIO_URL")??"";
        int w=request.Region.Width,h=request.Region.Height;
        Reference.Source=RepairBitmaps.Bitmap(request.ReferencePixels(),w,h);
        Before.Source=RepairBitmaps.Bitmap(RepairBitmaps.Preview(session.Document!,request,null),w,h);
        Status.Text=T("Ready");
        session.Changed+=SessionChanged;
        Closed+=(_,_)=>{closed=true;session.Changed-=SessionChanged;lifetime.Cancel();client?.Dispose();};
    }
    private void SessionChanged(object? sender,EventArgs args)
    {if(!request.IsCurrent(session)){ApplyButton.IsEnabled=GenerateButton.IsEnabled=false;Status.Text=T("Stale");}}

    private async void Connect_Click(object sender,RoutedEventArgs args)
    {
        ConnectButton.IsEnabled=false;
        try
        {
            client?.Dispose();client=new StudioRepairClient(Server.Text.Trim());
            Status.Text=T("Connecting");
            var choices=await client.Login(Email.Text.Trim(),Password.Password,lifetime.Token);
            Password.Clear();Workflow.ItemsSource=choices;Workflow.IsEnabled=choices.Count>0;
            if(choices.Count==0){Status.Text=T("NoWorkflow");ConnectButton.IsEnabled=true;return;}
            Workflow.SelectedIndex=0;LoginPanel.IsEnabled=false;Status.Text=T("Connected");
        }
        catch(Exception e){Failure(e);ConnectButton.IsEnabled=true;Password.Clear();}
    }
    private void Workflow_Changed(object sender,SelectionChangedEventArgs args)
    {
        fields.Clear();ParameterPanel.Children.Clear();
        if(Workflow.SelectedItem is not StudioWorkflow workflow)return;
        var defaults=workflow.Defaults(Math.Max(64,(request.Region.Width+7)/8*8),
            Math.Max(64,(request.Region.Height+7)/8*8),T("Instruction"));
        foreach(var p in workflow.Parameters.EnumerateObject())
        {
            var spec=p.Value;string role=spec.TryGetProperty("role",out var r)?r.GetString()??"":"";
            if(role is "managed_input" or "initial_image")continue;
            string type=spec.GetProperty("type").GetString()!;
            if(type=="ordered_loras")continue;
            var value=defaults.GetValueOrDefault(p.Name);
            ParameterPanel.Children.Add(new TextBlock{Text=spec.TryGetProperty("label",out var label)?label.GetString():p.Name,
                Margin=new Thickness(0,8,0,3),TextWrapping=TextWrapping.Wrap});
            if(spec.TryGetProperty("enum",out var options))
            {
                var combo=new ComboBox{ItemsSource=options.EnumerateArray().Select(x=>x.Clone()).ToArray(),MinHeight=28};
                combo.SelectedIndex=options.EnumerateArray().Select(x=>x.ToString()).ToList().IndexOf(value?.ToString()??"");
                ParameterPanel.Children.Add(combo);fields[p.Name]=()=>combo.SelectedItem;
            }
            else if(type=="boolean")
            {
                var check=new CheckBox{IsChecked=value?.ToString()?.Equals("true",StringComparison.OrdinalIgnoreCase)==true};
                ParameterPanel.Children.Add(check);fields[p.Name]=()=>check.IsChecked==true;
            }
            else
            {
                bool prompt=role is "prompt" or "positive_prompt" or "negative_prompt";
                var input=new TextBox{Text=value?.ToString()??"",MinHeight=prompt?72:28,AcceptsReturn=prompt,
                    TextWrapping=prompt?TextWrapping.Wrap:TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
                    MaxLength=spec.TryGetProperty("max_length",out var max)?Math.Min(20000,max.GetInt32()):20000};
                ParameterPanel.Children.Add(input);
                fields[p.Name]=()=>type switch{"integer"=>long.Parse(input.Text,CultureInfo.InvariantCulture),
                    "number"=>double.Parse(input.Text,CultureInfo.InvariantCulture),_=>input.Text};
            }
        }
        GenerateButton.IsEnabled=true;
    }
    private async void Generate_Click(object sender,RoutedEventArgs args)
    {
        if(client is null || Workflow.SelectedItem is not StudioWorkflow workflow || running)return;
        if(!request.IsCurrent(session)){Status.Text=T("Stale");return;}
        running=true;GenerateButton.IsEnabled=ApplyButton.IsEnabled=false;
        Workflow.IsEnabled=ParameterPanel.IsEnabled=false;
        try
        {
            if(!client.SubmissionBlocked)
            {
                var values=workflow.Defaults(64,64,T("Instruction"));
                foreach(var pair in fields)values[pair.Key]=pair.Value();
                (workWidth,workHeight)=workflow.Dimensions(values);
                var pixels=RepairBitmaps.Resize(request.ReferencePixels(),request.Region.Width,request.Region.Height,workWidth,workHeight);
                var png=RepairBitmaps.Png(pixels,workWidth,workHeight);
                Status.Text=T("Sending");await client.Submit(workflow,values,png,lifetime.Token);
            }
            else
            {
                if(client.ExecutionId is null)throw new InvalidOperationException("repair_submission_unknown");
                await client.Poll(lifetime.Token);
            }
            CancelJobButton.IsEnabled=true;
            while(client.State is "queued" or "running")
            {
                Status.Text=T("Running")+" "+client.ExecutionId;
                await Task.Delay(2000,lifetime.Token);await client.Poll(lifetime.Token);
            }
            CancelJobButton.IsEnabled=client.State is "unknown" or "submission_unknown";
            if(client.State!="completed"){Status.Text=T("JobState")+" "+client.State+" · "+client.ExecutionId;return;}
            Status.Text=T("Fetching");var result=await client.Result(lifetime.Token);
            var decoded=RepairBitmaps.Decode(result,workWidth,workHeight);
            if(!request.IsCurrent(session)){Status.Text=T("Stale");return;}
            request.SetCandidate(RepairBitmaps.Resize(decoded,workWidth,workHeight,request.Region.Width,request.Region.Height));
            After.Source=RepairBitmaps.Bitmap(RepairBitmaps.Preview(session.Document!,request,request.PreviewLayer(T("LayerName"))),request.Region.Width,request.Region.Height);
            hasCandidate=true;ApplyButton.IsEnabled=true;Status.Text=T("Compare");
        }
        catch(Exception e){Failure(e);}
        finally
        {
            running=false;
            if(!closed)
            {
                bool canResume=client.ExecutionId is not null && client.State is "queued" or "running" or "completed" or "unknown" or "submission_unknown";
                GenerateButton.Content=client.SubmissionBlocked?T("Resume"):T("Generate");
                GenerateButton.IsEnabled=!hasCandidate && request.IsCurrent(session) && (!client.SubmissionBlocked || canResume);
                Workflow.IsEnabled=ParameterPanel.IsEnabled=!client.SubmissionBlocked;
                CancelJobButton.IsEnabled=client.ExecutionId is not null && client.State is "queued" or "running" or "unknown";
            }
        }
    }
    private async void CancelJob_Click(object sender,RoutedEventArgs args)
    {
        if(client is null)return;CancelJobButton.IsEnabled=false;
        try{Status.Text=T("JobState")+" "+await client.Cancel(lifetime.Token)+" · "+client.ExecutionId;}
        catch(Exception e){Failure(e);}
    }
    private void Apply_Click(object sender,RoutedEventArgs args)
    {
        try{request.Commit(session,T("LayerName"));DialogResult=true;}
        catch(Exception e){Failure(e);ApplyButton.IsEnabled=false;}
    }
    private void Close_Click(object sender,RoutedEventArgs args)=>Close();
    private void Failure(Exception e)
    {
        if(closed || e is OperationCanceledException && lifetime.IsCancellationRequested)return;
        string code=e is StudioRepairException?"repair_http":e is OperationCanceledException?"repair_timeout":
            e is InvalidOperationException && e.Message.StartsWith("repair_",StringComparison.Ordinal)?e.Message:"repair_failed";
        Status.Text=T("Error")+" "+code;
        if(e is StudioRepairException http)Status.Text+=" · HTTP "+http.Status+(http.RequestId is {} id?" · "+id:"");
        if(client?.SubmissionBlocked==true)Status.Text+="\n"+T("Uncertain")+(client.ExecutionId is {} job?" · "+job:"");
        CutworkLog.Current.Warn("generation.repair","Image repair did not complete",new Dictionary<string,object?>{
            ["error"]=code,["httpStatus"]=(e as StudioRepairException)?.Status,["requestId"]=(e as StudioRepairException)?.RequestId});
    }
}
