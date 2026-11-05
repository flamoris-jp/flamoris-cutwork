using System.Net;
using System.Net.Http;
using System.Text.Json;
using Flamoris.Cutwork.App.Generation;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class StudioRepairClientTests
{
    private const string Descriptor="""
    {"id":"comfy-fixture","kind":"registered-comfy","name":"Fixture editor","selectable":true,
    "recipeSchema":10,"definitionVersion":1,"definitionDigest":"fixture-digest",
    "referenceRoles":["reference_image"],"image":{"mode":"declared"},"parameters":{
    "reference_image":{"type":"string","role":"managed_input","required":true},
    "instruction":{"type":"string","role":"prompt","min_length":1,"default":"old"},
    "width":{"type":"integer","role":"parameter","default":64},
    "height":{"type":"integer","role":"parameter","default":64}}}
    """;
    private static StudioWorkflow Workflow()=>new(JsonDocument.Parse(Descriptor).RootElement.Clone());
    private static HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,System.Text.Encoding.UTF8,"application/json")};
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request);}
    [TestMethod]
    public void EndpointRequiresHttpsOrLoopbackAndRejectsCredentialUrls()
    {
        Assert.AreEqual("https",StudioRepairClient.ValidateEndpoint("https://studio.example/").Scheme);
        Assert.AreEqual("http",StudioRepairClient.ValidateEndpoint("http://127.0.0.1:8000/").Scheme);
        foreach(string value in new[]{"http://studio.example/","https://user:password@studio.example/","https://studio.example/api/","https://studio.example/?key=secret","file:///tmp/a"})
            Assert.ThrowsExactly<InvalidOperationException>(()=>StudioRepairClient.ValidateEndpoint(value));
    }
    [TestMethod]
    public async Task LostSubmitAcknowledgementNeverResubmits()
    {
        int posts=0;Guid input=Guid.NewGuid();
        using var client=new StudioRepairClient("https://studio.example/",new Handler(async req=>
        {
            if(req.RequestUri!.AbsolutePath.EndsWith("/upload",StringComparison.Ordinal))return Json("{\"id\":\""+input+"\"}");
            Assert.AreEqual("/api/generation/image/jobs",req.RequestUri.AbsolutePath);posts++;
            var body=JsonDocument.Parse(await req.Content!.ReadAsStringAsync()).RootElement;
            Assert.AreEqual(input,body.GetProperty("imageInputs").GetProperty("reference_image").GetGuid());
            throw new HttpRequestException("lost acknowledgement");
        }));
        var wf=Workflow();var values=wf.Defaults(64,64,"fill");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(()=>client.Submit(wf,values,[1],CancellationToken.None));
        Assert.IsTrue(client.SubmissionBlocked);Assert.IsNull(client.ExecutionId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>client.Submit(wf,values,[1],CancellationToken.None));
        Assert.AreEqual(1,posts);
    }
    [TestMethod]
    public async Task LoginUsesSessionCsrfAndDescriptorDiscovery()
    {
        int calls=0;
        using var client=new StudioRepairClient("https://studio.example/",new Handler(req=>
        {
            calls++;string path=req.RequestUri!.AbsolutePath;
            if(path=="/api/session")return Task.FromResult(Json("{\"csrfToken\":\"csrf-fixture\",\"authenticated\":true}"));
            Assert.AreEqual("csrf-fixture",req.Headers.GetValues("X-CSRF-TOKEN").Single());
            if(path=="/api/auth/login")return Task.FromResult(Json("{}"));
            Assert.AreEqual("/api/generation/image/discovery",path);
            return Task.FromResult(Json("{\"managedInputReady\":true,\"workflows\":["+Descriptor+"]}"));
        }));
        var workflows=await client.Login("fixture@example.test","synthetic-password",CancellationToken.None);
        Assert.AreEqual(4,calls);Assert.AreEqual(1,workflows.Count);
        Assert.AreEqual("fill",workflows[0].Defaults(128,64,"fill")["instruction"]);
    }
    [TestMethod]
    public async Task RedirectAndOversizedBodyAreRejectedWithoutReadingDetails()
    {
        using var redirect=new StudioRepairClient("https://studio.example/",new Handler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect))));
        var e=await Assert.ThrowsExactlyAsync<StudioRepairException>(()=>redirect.Login("x","y",CancellationToken.None));Assert.AreEqual(302,e.Status);
        using var huge=new StudioRepairClient("https://studio.example/",new Handler(_=>Task.FromResult(Json(new string('x',2097153)))));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>huge.Login("x","y",CancellationToken.None));
    }
}
