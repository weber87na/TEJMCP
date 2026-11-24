using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace TejMcp;

public sealed record TejWatchlist(string Name, string[] Codes);

internal sealed class TejWatchlists(TejConfiguration configuration)
{
    internal const string DefaultName = "預設";

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions FileFormat = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private string PathToFile => configuration.WatchlistPath;

    internal TejWatchlist[] List() => Read().OrderBy(item => item.Key, NameComparer)
        .Select(item => new TejWatchlist(item.Key, item.Value)).ToArray();

    internal TejWatchlist Get(string name)
    {
        var key = NormalizeName(name);
        var lists = Read();
        var storedName = FindName(lists, key) ?? throw new TejException($"找不到觀察清單「{key}」。");
        return new TejWatchlist(storedName, lists[storedName]);
    }

    internal TejWatchlist Add(string name, string[] codes) => Change(name, codes, remove: false);

    internal TejWatchlist Remove(string name, string[] codes) => Change(name, codes, remove: true);

    private TejWatchlist Change(string name, string[] codes, bool remove)
    {
        name = NormalizeName(name);
        var requested = TejClient.CompanyCodes(codes);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathToFile)!);
            using var writerLock = LockForUpdate();
            var lists = Read();
            var storedName = FindName(lists, name) ?? name;
            var exists = lists.TryGetValue(storedName, out var current);
            if (!exists && remove)
                throw new TejException($"找不到觀察清單「{name}」。");

            var previous = current ?? Array.Empty<string>();
            var updated = remove ? previous.Except(requested).ToArray() : previous.Union(requested).ToArray();
            if (updated.Length > 30)
                throw new TejException("一份觀察清單最多可放 30 檔證券。請分成不同清單。");

            if (!exists || !previous.SequenceEqual(updated))
            {
                lists[storedName] = updated;
                Write(lists);
            }
            return new TejWatchlist(storedName, updated);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TejException("無法儲存觀察清單。請確認本機觀察清單檔案與資料夾可以寫入。");
        }
    }

    private Dictionary<string, string[]> Read()
    {
        var lists = new Dictionary<string, string[]>(NameComparer);
        try
        {
            using var stream = new FileStream(PathToFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var stored = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)
                ?? throw new JsonException();
            foreach (var item in stored)
            {
                var name = NormalizeName(item.Key);
                if (item.Value is null || lists.ContainsKey(name))
                    throw new JsonException();
                lists.Add(name, item.Value.Length == 0 ? [] : TejClient.CompanyCodes(item.Value));
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return lists;
        }
        catch (JsonException)
        {
            throw new TejException("本機觀察清單檔案的 JSON 格式無效。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new TejException("無法讀取本機觀察清單檔案。");
        }
        return lists;
    }

    private FileStream LockForUpdate()
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(PathToFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(3))
                    throw new TejException("觀察清單正在更新，請稍後再試。");
                Thread.Sleep(50);
            }
        }
    }

    private void Write(Dictionary<string, string[]> lists)
    {
        var staging = PathToFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, lists, FileFormat);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(PathToFile))
                File.Replace(staging, PathToFile, null);
            else
                File.Move(staging, PathToFile);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    private static string? FindName(Dictionary<string, string[]> lists, string name) =>
        lists.Keys.FirstOrDefault(key => NameComparer.Equals(key, name));

    private static string NormalizeName(string name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is < 1 or > 40 || trimmed.Any(char.IsControl))
            throw new TejException("觀察清單名稱須為 1 到 40 個字元。");
        return trimmed;
    }
}
