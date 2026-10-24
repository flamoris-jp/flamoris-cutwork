using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace Flamoris.Cutwork.Core;

public sealed record ArtworkWorkLogEntry(int Sequence, Guid Id, Guid RequestId,
    DateTimeOffset RecordedUtc, string DocumentToken, long DocumentRevision,
    string Decision, string Observation);

public sealed class WorkLogException(string code) : Exception(code)
{ public string Code { get; }=code; }

/// <summary>Append-only editorial evidence. No executable instructions or Undo authority.</summary>
public static class ArtworkWorkLog
{
    public const int MaximumEntries=4096, MaximumEntryBytes=32*1024, MaximumBytes=4*1024*1024;
    private static readonly UTF8Encoding Utf8=new(false,true);
    public static readonly JsonSerializerOptions Json = new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    public static byte[] Encode(ArtworkWorkLogEntry entry)=>JsonSerializer.SerializeToUtf8Bytes(entry,Json);
    public static int TextBytes(string decision,string observation)
    {
        if(decision is null || observation is null || string.IsNullOrWhiteSpace(decision+observation))
            throw new WorkLogException("work_log_text_required");
        try{return checked(Utf8.GetByteCount(decision)+Utf8.GetByteCount(observation));}
        catch(EncoderFallbackException){throw new WorkLogException("work_log_invalid_text");}
    }
    public static int Validate(IReadOnlyList<ArtworkWorkLogEntry> entries)
    {
        if(entries.Count>MaximumEntries)throw new WorkLogException("work_log_count_limit");
        int bytes=0;var ids=new HashSet<Guid>();var requests=new HashSet<Guid>();
        for(int i=0;i<entries.Count;i++)
        {
            var e=entries[i];
            if(e is null || e.Sequence!=i+1 || e.Id==Guid.Empty || e.RequestId==Guid.Empty
                || !ids.Add(e.Id) || !requests.Add(e.RequestId) || e.DocumentRevision<0
                || e.RecordedUtc.Offset!=TimeSpan.Zero || e.RecordedUtc==default
                || e.DocumentToken is null || e.DocumentToken.Length!=32
                || !Guid.TryParseExact(e.DocumentToken,"N",out var token) || token==Guid.Empty)
                throw new WorkLogException("work_log_invalid_entry");
            _=TextBytes(e.Decision,e.Observation);
            int entryBytes=Encode(e).Length+1;
            if(entryBytes>MaximumEntryBytes)throw new WorkLogException("work_log_entry_limit");
            bytes=checked(bytes+entryBytes);
            if(bytes>MaximumBytes)throw new WorkLogException("work_log_size_limit");
        }
        return bytes;
    }
}
