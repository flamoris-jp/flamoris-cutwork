using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flamoris.Cutwork.App.Generation;

internal sealed record StudioWorkflow(JsonElement Descriptor)
{
    public string Id => Descriptor.GetProperty("id").GetString()!;
    public string Name => Descriptor.GetProperty("name").GetString()!;
    public bool Declared => Descriptor.GetProperty("recipeSchema").GetInt32() == 10;
    public JsonElement Parameters => Descriptor.GetProperty("parameters");
    public override string ToString() => Name;
    public static bool Supported(JsonElement d)
    {
        if (!d.TryGetProperty("selectable",out var ready) || ready.ValueKind!=JsonValueKind.True
            || d.GetProperty("kind").GetString()!="registered-comfy"
            || !d.TryGetProperty("referenceRoles",out var roles) || roles.GetArrayLength()!=1) return false;
        int schema=d.GetProperty("recipeSchema").GetInt32();
        if(schema==7) return roles[0].GetString()=="initial_image" && d.GetProperty("image").GetProperty("mode").GetString()=="img2img";
        return schema==10 && d.GetProperty("parameters").TryGetProperty("width",out var w)
            && w.GetProperty("type").GetString()=="integer"
            && d.GetProperty("parameters").TryGetProperty("height",out var h) && h.GetProperty("type").GetString()=="integer";
    }

    public Dictionary<string,object?> Defaults(int width,int height,string instruction)
    {
        var values=new Dictionary<string,object?>(); bool promptSet=false;
        foreach(var p in Parameters.EnumerateObject())
        {
            string? role=p.Value.TryGetProperty("role",out var r)?r.GetString():null;
            if(role is "managed_input" or "initial_image")continue;
            if(p.Value.TryGetProperty("default",out var v))values[p.Name]=v.Clone();
            if(role=="width" || Declared && p.Name=="width")values[p.Name]=width;
            if(role=="height" || Declared && p.Name=="height")values[p.Name]=height;
            if(role=="positive_prompt" || !promptSet && role=="prompt"
                && p.Value.TryGetProperty("min_length",out var min) && min.GetInt32()>0)
            {values[p.Name]=instruction;promptSet=true;}
        }
        return values;
    }

    public object Request(Dictionary<string,object?> values,Guid input)
    {
        if(Declared)return new{workflowId=Id,workflowKind="registered-comfy",
            definitionVersion=Descriptor.GetProperty("definitionVersion").GetInt32(),
            definitionDigest=Descriptor.GetProperty("definitionDigest").GetString(),
            additionalParameters=values,imageInputs=new Dictionary<string,Guid>{{Descriptor.GetProperty("referenceRoles")[0].GetString()!,input}}};
        var request=new Dictionary<string,object?>{{"workflowId",Id},{"workflowKind","registered-comfy"},
            {"definitionVersion",null},{"definitionDigest",Descriptor.GetProperty("definitionDigest").GetString()},
            {"referenceInputId",input},{"loras",Array.Empty<object>()},{"negativePrompt",""}};
        var extras=new Dictionary<string,object?>();
        var fields=new Dictionary<string,string>{{"positive_prompt","positivePrompt"},{"negative_prompt","negativePrompt"},
            {"width","width"},{"height","height"},{"checkpoint","checkpoint"},{"seed","seed"},{"steps","steps"},
            {"cfg","cfg"},{"sampler","sampler"},{"scheduler","scheduler"},{"denoise","denoise"},{"loras","loras"}};
        foreach(var p in Parameters.EnumerateObject())if(values.TryGetValue(p.Name,out var value))
        {
            string? role=p.Value.TryGetProperty("role",out var r)?r.GetString():null;
            if(role is not null && fields.TryGetValue(role,out var field))request[field]=value;
            else if(role is null)extras[p.Name]=value;
        }
        request["additionalParameters"]=extras;return request;
    }

    public (int Width,int Height) Dimensions(Dictionary<string,object?> values)
    {
        string Key(string role)=>Declared?role:Parameters.EnumerateObject().Single(p=>p.Value.TryGetProperty("role",out var r) && r.GetString()==role).Name;
        int w=JsonSerializer.SerializeToElement(values[Key("width")]).GetInt32();
        int h=JsonSerializer.SerializeToElement(values[Key("height")]).GetInt32();
        if(w<64 || h<64 || w>2048 || h>2048 || (long)w*h>1048576 || w%8!=0 || h%8!=0)
            throw new InvalidOperationException("repair_dimensions");
        return(w,h);
    }
}

internal sealed class StudioRepairClient : IDisposable
{
    private readonly HttpClient http;
    private string? csrf;
    public Guid? ExecutionId { get; private set; }
    public bool SubmissionBlocked { get; private set; }
    public string State { get; private set; }="";
    public static Uri ValidateEndpoint(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath!="/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !(uri.Scheme=="https" || uri.Scheme=="http" && uri.IsLoopback))
            throw new InvalidOperationException("repair_endpoint");
        return uri;
    }
    public StudioRepairClient(string endpoint,HttpMessageHandler? handler=null)
    {
        http=new(handler ?? new HttpClientHandler{AllowAutoRedirect=false,CookieContainer=new CookieContainer(),UseCookies=true});
        http.BaseAddress=ValidateEndpoint(endpoint);http.Timeout=TimeSpan.FromSeconds(100);
    }
    private async Task<(byte[] Bytes,string? Mime)> Exchange(HttpMethod method,string path,HttpContent? content,CancellationToken ct,int limit=2097152)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(100));
        using var req=new HttpRequestMessage(method,path){Content=content};
        if(csrf is not null)req.Headers.Add("X-CSRF-TOKEN",csrf);
        req.Headers.CacheControl=new CacheControlHeaderValue{NoStore=true};
        using var response=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
        if(!response.IsSuccessStatusCode)
            throw new StudioRepairException((int)response.StatusCode,response.Headers.TryGetValues("X-Request-ID",out var ids)?ids.FirstOrDefault():null);
        if(response.Content.Headers.ContentLength>limit)throw new InvalidOperationException("repair_response_limit");
        using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output=new MemoryStream();var buffer=new byte[16384];int n;
        while((n=await stream.ReadAsync(buffer,deadline.Token))>0)
        {if(output.Length+n>limit)throw new InvalidOperationException("repair_response_limit");await output.WriteAsync(buffer.AsMemory(0,n),deadline.Token);}
        return(output.ToArray(),response.Content.Headers.ContentType?.MediaType);
    }
    private async Task<JsonElement> Json(HttpMethod method,string path,object? body,CancellationToken ct)
    {
        var result=await Exchange(method,path,body is null?null:JsonContent.Create(body),ct);
        using var doc=JsonDocument.Parse(result.Bytes,new JsonDocumentOptions{MaxDepth=32});return doc.RootElement.Clone();
    }
    public async Task<IReadOnlyList<StudioWorkflow>> Login(string email,string password,CancellationToken ct)
    {
        var session=await Json(HttpMethod.Get,"api/session",null,ct);csrf=session.GetProperty("csrfToken").GetString();
        await Json(HttpMethod.Post,"api/auth/login",new{email,password},ct);
        session=await Json(HttpMethod.Get,"api/session",null,ct);
        if(!session.GetProperty("authenticated").GetBoolean())throw new InvalidOperationException("repair_login");
        csrf=session.GetProperty("csrfToken").GetString();
        var catalog=await Json(HttpMethod.Get,"api/generation/image/discovery",null,ct);
        if(!catalog.GetProperty("managedInputReady").GetBoolean())return [];
        return catalog.GetProperty("workflows").EnumerateArray().Where(StudioWorkflow.Supported).Select(d=>new StudioWorkflow(d.Clone())).ToArray();
    }
    public async Task Submit(StudioWorkflow workflow,Dictionary<string,object?> values,byte[] png,CancellationToken ct)
    {
        if(SubmissionBlocked)throw new InvalidOperationException("repair_submission_unknown");
        if(png.Length is 0 or >8388608)throw new InvalidOperationException("repair_response_limit");
        using var content=new ByteArrayContent(png);content.Headers.ContentType=new MediaTypeHeaderValue("image/png");
        var upload=await Exchange(HttpMethod.Post,"api/generation/inputs/upload",content,ct);
        using var doc=JsonDocument.Parse(upload.Bytes);Guid input=doc.RootElement.GetProperty("id").GetGuid();
        SubmissionBlocked=true; ExecutionId=null;State="submission_unknown";
        // This POST is non-idempotent. Any missing/uncertain acknowledgement keeps the fence.
        var result=await Json(HttpMethod.Post,"api/generation/image/jobs",workflow.Request(values,input),ct);
        ExecutionId=result.GetProperty("id").GetGuid();State=result.GetProperty("state").GetString()!;
    }
    public async Task<string> Poll(CancellationToken ct)
    {
        if(ExecutionId is not {} id)throw new InvalidOperationException("repair_submission_unknown");
        var result=await Json(HttpMethod.Get,$"api/executions/{id:D}",null,ct);State=result.GetProperty("state").GetString()!;
        return State;
    }
    public async Task<byte[]> Result(CancellationToken ct)
    {
        if(ExecutionId is not {} id || State!="completed")throw new InvalidOperationException("repair_no_candidate");
        var result=await Json(HttpMethod.Get,$"api/executions/{id:D}/result",null,ct);
        var images=result.GetProperty("assets").EnumerateArray().Where(a=>a.GetProperty("mimeType").GetString()=="image/png").ToArray();
        if(images.Length!=1)throw new InvalidOperationException("repair_output_count");
        Guid asset=images[0].GetProperty("id").GetGuid();
        var data=await Exchange(HttpMethod.Get,$"api/executions/{id:D}/assets/{asset:D}/content",null,ct,8388608);
        if(data.Mime!="image/png")throw new InvalidOperationException("repair_dimensions");
        return data.Bytes;
    }
    public async Task<string> Cancel(CancellationToken ct)
    {
        if(ExecutionId is not {} id)throw new InvalidOperationException("repair_submission_unknown");
        var result=await Json(HttpMethod.Post,$"api/executions/{id:D}/cancel",new{},ct);State=result.GetProperty("state").GetString()!;return State;
    }
    public void Dispose()=>http.Dispose();
}

internal sealed class StudioRepairException(int status,string? requestId) : Exception("repair_http")
{
    public int Status { get; }=status;
    // Do not publish untrusted response bodies, credentials, prompts or URLs into logs.
    public string? RequestId { get; }=requestId is {Length:<=80} && requestId.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_')?requestId:null;
}
