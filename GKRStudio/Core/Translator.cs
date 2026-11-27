using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GKRStudio.Core;

/// <summary>
/// SentencePiece "unigram" tokenizer read straight from a .spm model file (a small protobuf):
/// the text is split into the pieces with the best total score (Viterbi).
/// </summary>
public sealed class SentencePiece
{
    private readonly Dictionary<string, float> pieces = new(StringComparer.Ordinal);
    private readonly int maxLen;
    private readonly float unkScore;

    private SentencePiece(Dictionary<string, float> p)
    {
        pieces = p;
        maxLen = p.Keys.Max(k => k.Length);
        unkScore = p.Values.Min() - 10;
    }

    public static SentencePiece Load(string path)
    {
        var data = File.ReadAllBytes(path);
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        int pos = 0;
        while (pos < data.Length)
        {
            ulong key = Varint(data, ref pos);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            if (field == 1 && wire == 2) // ModelProto.pieces
            {
                int len = (int)Varint(data, ref pos), end = pos + len;
                string? piece = null; float score = 0; int type = 1;
                while (pos < end)
                {
                    ulong k = Varint(data, ref pos);
                    int f = (int)(k >> 3), w = (int)(k & 7);
                    if (f == 1 && w == 2) { int l = (int)Varint(data, ref pos); piece = Encoding.UTF8.GetString(data, pos, l); pos += l; }
                    else if (f == 2 && w == 5) { score = BitConverter.ToSingle(data, pos); pos += 4; }
                    else if (f == 3 && w == 0) type = (int)Varint(data, ref pos);
                    else Skip(data, ref pos, w);
                }
                pos = end;
                if (piece != null && (type == 1 || type == 4)) result[piece] = score; // normal + user-defined
            }
            else Skip(data, ref pos, wire);
        }
        if (result.Count == 0) throw new InvalidDataException("Geçersiz dil modeli (spm).");
        return new SentencePiece(result);
    }

    private static ulong Varint(byte[] d, ref int pos)
    {
        ulong v = 0; int shift = 0;
        while (true) { byte b = d[pos++]; v |= (ulong)(b & 0x7F) << shift; if (b < 0x80) return v; shift += 7; }
    }

    private static void Skip(byte[] d, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0: Varint(d, ref pos); break;
            case 1: pos += 8; break;
            case 2: { int len = (int)Varint(d, ref pos); pos += len; break; } // (pos += Varint(ref pos) would use the old pos)
            case 5: pos += 4; break;
            default: throw new InvalidDataException("Geçersiz dil modeli (spm).");
        }
    }

    public List<string> Encode(string text)
    {
        string s = "▁" + Regex.Replace(text.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", "▁");
        int n = s.Length;
        var best = new float[n + 1];
        var from = new int[n + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;
        for (int end = 1; end <= n; end++)
        {
            for (int len = 1; len <= Math.Min(maxLen, end); len++)
            {
                int start = end - len;
                if (float.IsNegativeInfinity(best[start])) continue;
                float score = pieces.TryGetValue(s.Substring(start, len), out var sc) ? sc : len == 1 ? unkScore : float.NaN;
                if (float.IsNaN(score)) continue;
                if (best[start] + score > best[end]) { best[end] = best[start] + score; from[end] = start; }
            }
        }
        var result = new List<string>();
        for (int end = n; end > 0; end = from[end]) result.Add(s[from[end]..end]);
        result.Reverse();
        return result;
    }
}

/// <summary>One offline Marian (OPUS-MT) translation model: ONNX encoder + decoder, shared vocabulary.</summary>
public sealed class MarianModel : IDisposable
{
    private readonly InferenceSession encoder, decoder;
    private readonly SentencePiece source;
    private readonly Dictionary<string, int> vocab;
    private readonly string[] pieceOf;
    private readonly int eos, unk, pad, start;
    private readonly string decIds, decMask, decStates;

    public MarianModel(string dir)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        options.IntraOpNumThreads = 2; // several sentences run side by side (TranslateTools)
        encoder = new InferenceSession(Path.Combine(dir, "encoder.onnx"), options);
        decoder = new InferenceSession(Path.Combine(dir, "decoder.onnx"), options);
        source = SentencePiece.Load(Path.Combine(dir, "source.spm"));
        vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(dir, "vocab.json")))!;
        pieceOf = new string[vocab.Values.Max() + 1];
        foreach (var (p, id) in vocab) pieceOf[id] = p;
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config.json")));
        var root = config.RootElement;
        eos = root.TryGetProperty("eos_token_id", out var e) ? e.GetInt32() : vocab.GetValueOrDefault("</s>");
        pad = root.TryGetProperty("pad_token_id", out var p2) ? p2.GetInt32() : vocab.GetValueOrDefault("<pad>");
        start = root.TryGetProperty("decoder_start_token_id", out var s) ? s.GetInt32() : pad;
        unk = vocab.GetValueOrDefault("<unk>", 1);
        string Input(string part) => decoder.InputMetadata.Keys.First(k => k.Contains(part, StringComparison.Ordinal));
        decStates = Input("encoder_hidden_states");
        decMask = Input("encoder_attention_mask");
        decIds = decoder.InputMetadata.Keys.First(k => k == "input_ids" || (k.Contains("input_ids") && !k.Contains("encoder")));
    }

    /// <summary>Translates one sentence (greedy decoding).</summary>
    public string Translate(string sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence)) return sentence;
        var ids = source.Encode(sentence).Select(pc => vocab.TryGetValue(pc, out int id) ? id : unk).Take(480).Append(eos).Select(i => (long)i).ToArray();
        int n = ids.Length;
        var inputIds = new DenseTensor<long>(ids, [1, n]);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), [1, n]);
        using var encOut = encoder.Run([NamedOnnxValue.CreateFromTensor("input_ids", inputIds), NamedOnnxValue.CreateFromTensor("attention_mask", mask)]);
        var states = encOut.First().AsTensor<float>().ToDenseTensor();

        var output = new List<long> { start };
        int limit = Math.Min(512, n * 2 + 16);
        for (int step = 0; step < limit; step++)
        {
            var dec = new DenseTensor<long>(output.ToArray(), [1, output.Count]);
            using var res = decoder.Run([
                NamedOnnxValue.CreateFromTensor(decIds, dec),
                NamedOnnxValue.CreateFromTensor(decMask, mask),
                NamedOnnxValue.CreateFromTensor(decStates, states)]);
            var logits = res.First().AsTensor<float>();
            int vocabSize = logits.Dimensions[2], last = output.Count - 1;
            var dense = logits as DenseTensor<float> ?? logits.ToDenseTensor();
            var row = dense.Buffer.Span.Slice(last * vocabSize, vocabSize);
            int bestId = eos; float bestScore = float.NegativeInfinity;
            for (int v = 0; v < row.Length; v++)
            {
                if (v == pad) continue;
                if (row[v] > bestScore) { bestScore = row[v]; bestId = v; }
            }
            if (bestId == eos) break;
            output.Add(bestId);
            // stop runaway repetition ("the the the ...")
            if (output.Count > 8 && output.Skip(output.Count - 4).Distinct().Count() == 1) break;
        }
        var sb = new StringBuilder();
        foreach (var id in output.Skip(1))
        {
            string? pc = id < pieceOf.Length ? pieceOf[id] : null;
            if (pc == null || pc.StartsWith('<') && pc.EndsWith('>')) continue;
            sb.Append(pc);
        }
        return sb.ToString().Replace('▁', ' ').Trim();
    }

    public void Dispose() { encoder.Dispose(); decoder.Dispose(); }
}
