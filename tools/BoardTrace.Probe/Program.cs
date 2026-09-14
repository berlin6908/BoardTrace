using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using BoardTrace.Vision;
using Microsoft.Data.SqlClient;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

if (args.Length == 0) throw new ArgumentException("Usage: vision <test> <reference> | onnx <model> <sha256> <tested> <reference> | sql");
object report;
switch (args[0])
{
    case "vision":
        using (var tested = Cv2.ImDecode(File.ReadAllBytes(args[1]), ImreadModes.Grayscale))
        using (var reference = Cv2.ImDecode(File.ReadAllBytes(args[2]), ImreadModes.Grayscale))
        using (var difference = new Mat())
        {
            if (tested.Empty() || reference.Empty() || tested.Size() != reference.Size())
                throw new InvalidDataException("Images must decode and have the same dimensions.");
            var watch = Stopwatch.StartNew();
            Cv2.Absdiff(tested, reference, difference);
            Cv2.Threshold(difference, difference, 80, 255, ThresholdTypes.Binary);
            report = new { probe = "opencv", version = Cv2.GetVersionString(), tested.Width, tested.Height,
                changedPixels = Cv2.CountNonZero(difference), elapsedMs = watch.Elapsed.TotalMilliseconds };
        }
        break;
    case "onnx":
        if (args.Length != 5) throw new ArgumentException("onnx <model> <sha256> <tested> <reference>");
    using (var detector = new OnnxDetector(File.ReadAllBytes(args[1]), args[2]))
        {
            var tested = File.ReadAllBytes(args[3]);
            var reference = File.ReadAllBytes(args[4]);
            // Keep every candidate in the exported graph's original order for comparison.
            var result = detector.Detect(tested, reference, new OnnxScoreThresholds(0, 0, 0, 0, 0, 0));
            report = new { probe = "onnx", runtime = OrtEnv.Instance().GetVersionString(),
                modelSha256 = detector.ModelSha256,
                imageSha256 = Convert.ToHexStringLower(SHA256.HashData(tested)),
                referenceSha256 = Convert.ToHexStringLower(SHA256.HashData(reference)),
                elapsedMs = result.ElapsedMs, sessionInitializationMs = detector.SessionInitializationMs,
                timingDescription = "ElapsedMs includes paired decode, preprocessing, CPU inference and output validation; excludes file reads, SHA verification and session construction.",
                boxes = result.Defects.SelectMany(defect => defect.Box).ToArray(),
                labels = result.Defects.Select(defect => defect.ClassId!.Value).ToArray(),
                scores = result.Defects.Select(defect => defect.Score).ToArray() };
        }
        break;
    case "sql":
        var connectionString = Environment.GetEnvironmentVariable("BOARDTRACE_SQL")
            ?? @"Server=(localdb)\BoardTrace;Database=master;Integrated Security=true;TrustServerCertificate=true";
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "IF DB_ID(N'BoardTrace_Probe') IS NULL CREATE DATABASE BoardTrace_Probe";
            await command.ExecuteNonQueryAsync();
            await connection.ChangeDatabaseAsync("BoardTrace_Probe");
            command.CommandText = "IF OBJECT_ID(N'dbo.Probe') IS NULL CREATE TABLE dbo.Probe (Id uniqueidentifier PRIMARY KEY, Image varbinary(max) NOT NULL)";
            await command.ExecuteNonQueryAsync();
            var identity = Guid.NewGuid();
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT dbo.Probe (Id, Image) VALUES (@id, 0x010203)";
                command.Parameters.AddWithValue("@id", identity);
                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE dbo.Probe WHERE Id=@id";
                await command.ExecuteNonQueryAsync();
                await transaction.RollbackAsync();
            }
            command.Transaction = null;
            command.CommandText = "SELECT COUNT(*) FROM dbo.Probe WHERE Id=@id AND Image=0x010203";
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 1)
                throw new InvalidOperationException("SQL commit/rollback verification failed.");
            command.CommandText = "SELECT @@VERSION";
            report = new { probe = "sql", version = await command.ExecuteScalarAsync(), commitAndRollbackVerified = true };
        }
        break;
    default: throw new ArgumentException("Unknown probe.");
}
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
