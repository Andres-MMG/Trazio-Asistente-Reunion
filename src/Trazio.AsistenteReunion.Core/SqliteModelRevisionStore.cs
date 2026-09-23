using System.Text;
using Microsoft.Data.Sqlite;

namespace Trazio.AsistenteReunion.Core;

public sealed partial class SqliteSessionStore
{
    private async Task InitializeModelRevisionSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS transcript_model_revisions (
              id TEXT PRIMARY KEY,
              session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
              source INTEGER NOT NULL,
              status INTEGER NOT NULL,
              model_nonce BLOB NOT NULL, model_cipher BLOB NOT NULL, model_tag BLOB NOT NULL,
              model_hash_nonce BLOB NULL, model_hash_cipher BLOB NULL, model_hash_tag BLOB NULL,
              language TEXT NOT NULL,
              started_at TEXT NOT NULL, ended_at TEXT NULL, processing_ms INTEGER NULL,
              error_nonce BLOB NULL, error_cipher BLOB NULL, error_tag BLOB NULL,
              glossary_version_nonce BLOB NOT NULL, glossary_version_cipher BLOB NOT NULL, glossary_version_tag BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_model_revisions_session_source ON transcript_model_revisions(session_id,source,started_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_model_revision_running ON transcript_model_revisions(session_id,source) WHERE status=0;
            CREATE TABLE IF NOT EXISTS transcript_model_revision_segments (
              id TEXT PRIMARY KEY,
              revision_id TEXT NOT NULL REFERENCES transcript_model_revisions(id) ON DELETE CASCADE,
              sequence INTEGER NOT NULL,
              start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL,
              text_nonce BLOB NOT NULL, text_cipher BLOB NOT NULL, text_tag BLOB NOT NULL,
              UNIQUE(revision_id,sequence));
            CREATE INDEX IF NOT EXISTS ix_model_revision_segments ON transcript_model_revision_segments(revision_id,sequence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await RecoverStaleModelRevisionsAsync(connection, cancellationToken);
    }

    public async Task<TranscriptModelRevision> StartModelRevisionAsync(string sessionId, AudioSourceKind source, string modelIdentity, string? modelHash, string language, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelIdentity) || string.IsNullOrWhiteSpace(language)) throw new ArgumentException("Se requieren la identidad del modelo y el idioma.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var session = connection.CreateCommand();
        session.CommandText = "SELECT state FROM sessions WHERE id=$id";
        session.Parameters.AddWithValue("$id", sessionId);
        var state = await session.ExecuteScalarAsync(cancellationToken);
        if (state is null) throw new InvalidOperationException("La sesión guardada ya no existe.");
        if ((SessionState)Convert.ToInt32(state) is SessionState.Recording or SessionState.Paused) throw new InvalidOperationException("Detén la sesión activa antes de retranscribirla.");
        await using var running = connection.CreateCommand();
        running.CommandText = "SELECT COUNT(*) FROM transcript_model_revisions WHERE session_id=$session AND source=$source AND status=$running";
        running.Parameters.AddWithValue("$session", sessionId); running.Parameters.AddWithValue("$source", (int)source); running.Parameters.AddWithValue("$running", (int)ModelRevisionStatus.Running);
        if (Convert.ToInt32(await running.ExecuteScalarAsync(cancellationToken)) > 0) throw new InvalidOperationException("Ya hay una retranscripción en curso para esta sesión y fuente.");
        var revision = new TranscriptModelRevision(Guid.NewGuid().ToString("N"), sessionId, source, ModelRevisionStatus.Running, modelIdentity.Trim(), modelHash, language, DateTimeOffset.UtcNow, null, null, null, "none");
        var model = protector.Protect(Encoding.UTF8.GetBytes(revision.ModelIdentity), $"model-revision:{revision.Id}:model");
        var hash = ProtectRevisionOptional(revision.ModelHash, $"model-revision:{revision.Id}:hash");
        var glossary = protector.Protect(Encoding.UTF8.GetBytes(revision.GlossaryPromptVersion), $"model-revision:{revision.Id}:glossary");
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO transcript_model_revisions(id,session_id,source,status,model_nonce,model_cipher,model_tag,model_hash_nonce,model_hash_cipher,model_hash_tag,language,started_at,glossary_version_nonce,glossary_version_cipher,glossary_version_tag)
            VALUES($id,$session,$source,$status,$mn,$mc,$mt,$hn,$hc,$ht,$language,$started,$gn,$gc,$gt)
            """;
        insert.Parameters.AddWithValue("$id", revision.Id); insert.Parameters.AddWithValue("$session", sessionId); insert.Parameters.AddWithValue("$source", (int)source); insert.Parameters.AddWithValue("$status", (int)revision.Status);
        AddRevisionPayload(insert,"m",model); AddRevisionOptionalPayload(insert,"h",hash); AddRevisionPayload(insert,"g",glossary);
        insert.Parameters.AddWithValue("$language", language); insert.Parameters.AddWithValue("$started", revision.StartedAt.ToString("O"));
        try { await insert.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        { throw new InvalidOperationException("Ya hay una retranscripción en curso para esta sesión y fuente.", ex); }
        return revision;
    }

    public async Task SetModelRevisionVerifiedModelHashAsync(
        string revisionId,
        string verifiedModelHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedModelHash);
        var encryptedHash = protector.Protect(
            Encoding.UTF8.GetBytes(verifiedModelHash.Trim()),
            $"model-revision:{revisionId}:hash");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE transcript_model_revisions SET model_hash_nonce=$hn,model_hash_cipher=$hc,model_hash_tag=$ht WHERE id=$id AND status=$running";
        command.Parameters.AddWithValue("$id", revisionId);
        command.Parameters.AddWithValue("$running", (int)ModelRevisionStatus.Running);
        AddRevisionPayload(command, "h", encryptedHash);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("La versión del modelo ya no está en ejecución.");
    }
    public async Task SaveModelRevisionSegmentAsync(TranscriptModelRevisionSegment segment, CancellationToken cancellationToken = default)
    {
        var text = protector.Protect(Encoding.UTF8.GetBytes(segment.Text), $"model-revision-segment:{segment.Id}:text");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO transcript_model_revision_segments(id,revision_id,sequence,start_ms,end_ms,text_nonce,text_cipher,text_tag) VALUES($id,$revision,$sequence,$start,$end,$n,$c,$t)";
        command.Parameters.AddWithValue("$id",segment.Id); command.Parameters.AddWithValue("$revision",segment.RevisionId); command.Parameters.AddWithValue("$sequence",segment.Sequence); command.Parameters.AddWithValue("$start",(long)segment.Start.TotalMilliseconds); command.Parameters.AddWithValue("$end",(long)segment.End.TotalMilliseconds); AddPayload(command,text);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FinishModelRevisionAsync(string revisionId, ModelRevisionStatus status, DateTimeOffset endedAt, TimeSpan duration, string? error, CancellationToken cancellationToken = default)
    {
        if (status == ModelRevisionStatus.Running) throw new ArgumentException("Se requiere un estado final.", nameof(status));
        var encryptedError = ProtectRevisionOptional(error, $"model-revision:{revisionId}:error");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE transcript_model_revisions SET status=$status,ended_at=$ended,processing_ms=$duration,error_nonce=$en,error_cipher=$ec,error_tag=$et WHERE id=$id AND status=$running";
        command.Parameters.AddWithValue("$id",revisionId); command.Parameters.AddWithValue("$status",(int)status); command.Parameters.AddWithValue("$ended",endedAt.ToString("O")); command.Parameters.AddWithValue("$duration",(long)duration.TotalMilliseconds); command.Parameters.AddWithValue("$running",(int)ModelRevisionStatus.Running); AddRevisionOptionalPayload(command,"e",encryptedError);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("La versión del modelo ya no está en ejecución.");
    }

    public async Task<IReadOnlyList<TranscriptModelRevision>> ListModelRevisionsAsync(string sessionId, AudioSourceKind? source = null, bool successfulOnly = false, CancellationToken cancellationToken = default)
    {
        var result = new List<TranscriptModelRevision>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,source,status,model_nonce,model_cipher,model_tag,model_hash_nonce,model_hash_cipher,model_hash_tag,language,started_at,ended_at,processing_ms,error_nonce,error_cipher,error_tag,glossary_version_nonce,glossary_version_cipher,glossary_version_tag FROM transcript_model_revisions WHERE session_id=$session" + (source is null ? "" : " AND source=$source") + (successfulOnly ? " AND status=$success" : "") + " ORDER BY started_at,id";
        command.Parameters.AddWithValue("$session",sessionId); if(source is not null) command.Parameters.AddWithValue("$source",(int)source.Value); if(successfulOnly) command.Parameters.AddWithValue("$success",(int)ModelRevisionStatus.Succeeded);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken)) { var id=reader.GetString(0); result.Add(new(id,sessionId,(AudioSourceKind)reader.GetInt32(1),(ModelRevisionStatus)reader.GetInt32(2),UnprotectRevisionRequired(reader,3,$"model-revision:{id}:model"),UnprotectRevisionOptional(reader,6,$"model-revision:{id}:hash"),reader.GetString(9),DateTimeOffset.Parse(reader.GetString(10)),reader.IsDBNull(11)?null:DateTimeOffset.Parse(reader.GetString(11)),reader.IsDBNull(12)?null:TimeSpan.FromMilliseconds(reader.GetInt64(12)),UnprotectRevisionOptional(reader,13,$"model-revision:{id}:error"),UnprotectRevisionRequired(reader,16,$"model-revision:{id}:glossary"))); }
        return result;
    }

    public async Task<IReadOnlyList<TranscriptModelRevisionSegment>> GetModelRevisionSegmentsAsync(string revisionId, CancellationToken cancellationToken = default)
    {
        var result=new List<TranscriptModelRevisionSegment>(); await using var connection=await OpenAsync(cancellationToken); await using var command = connection.CreateCommand(); command.CommandText="SELECT id,sequence,start_ms,end_ms,text_nonce,text_cipher,text_tag FROM transcript_model_revision_segments WHERE revision_id=$id ORDER BY sequence"; command.Parameters.AddWithValue("$id",revisionId); await using var reader=await command.ExecuteReaderAsync(cancellationToken); while(await reader.ReadAsync(cancellationToken)){var id=reader.GetString(0); result.Add(new(id,revisionId,reader.GetInt64(1),TimeSpan.FromMilliseconds(reader.GetInt64(2)),TimeSpan.FromMilliseconds(reader.GetInt64(3)),UnprotectRevisionRequired(reader,4,$"model-revision-segment:{id}:text")));} return result;
    }

    private async Task RecoverStaleModelRevisionsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT id FROM transcript_model_revisions WHERE status=$running";
        lookup.Parameters.AddWithValue("$running", (int)ModelRevisionStatus.Running);
        await using (var reader = await lookup.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        if (ids.Count == 0) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in ids)
        {
            var diagnostic = protector.Protect(Encoding.UTF8.GetBytes("Interrumpida porque la aplicación se cerró antes de completar la retranscripción."), $"model-revision:{id}:error");
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE transcript_model_revisions SET status=$failed,ended_at=$ended,processing_ms=COALESCE(processing_ms,0),error_nonce=$en,error_cipher=$ec,error_tag=$et WHERE id=$id AND status=$running";
            update.Parameters.AddWithValue("$failed", (int)ModelRevisionStatus.Failed);
            update.Parameters.AddWithValue("$ended", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$running", (int)ModelRevisionStatus.Running);
            AddRevisionPayload(update, "e", diagnostic);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
    private EncryptedPayload? ProtectRevisionOptional(string? value,string purpose)=>string.IsNullOrWhiteSpace(value)?null:protector.Protect(Encoding.UTF8.GetBytes(value),purpose);
    private string? UnprotectRevisionOptional(SqliteDataReader reader,int offset,string purpose)=>reader.IsDBNull(offset)?null:UnprotectRevisionRequired(reader,offset,purpose);
    private string UnprotectRevisionRequired(SqliteDataReader reader,int offset,string purpose)=>Encoding.UTF8.GetString(protector.Unprotect(ReadPayload(reader,offset),purpose));
    private static void AddRevisionPayload(SqliteCommand c,string p,EncryptedPayload v){c.Parameters.AddWithValue($"${p}n",v.Nonce);c.Parameters.AddWithValue($"${p}c",v.Ciphertext);c.Parameters.AddWithValue($"${p}t",v.Tag);}
    private static void AddRevisionOptionalPayload(SqliteCommand c,string p,EncryptedPayload? v){c.Parameters.AddWithValue($"${p}n",v is null?DBNull.Value:v.Nonce);c.Parameters.AddWithValue($"${p}c",v is null?DBNull.Value:v.Ciphertext);c.Parameters.AddWithValue($"${p}t",v is null?DBNull.Value:v.Tag);}
}



