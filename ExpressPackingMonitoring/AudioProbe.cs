using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using ExpressPackingMonitoring.ViewModels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ExpressPackingMonitoring
{
    internal static class AudioProbe
    {
        public static bool TryHandleCommandLine(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (!args.Any(a => string.Equals(a, "--audio-probe", StringComparison.OrdinalIgnoreCase)))
                return false;

            int seconds = ParseSeconds(args);
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio_probe.log");
            try
            {
                var result = Run(seconds, logPath);
                exitCode = result ? 0 : 2;
            }
            catch (Exception ex)
            {
                File.WriteAllText(logPath, $"Audio probe exception: {ex}\r\n", Encoding.UTF8);
                exitCode = 1;
            }
            return true;
        }

        private static int ParseSeconds(string[] args)
        {
            int index = Array.FindIndex(args, a => string.Equals(a, "--audio-probe", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int seconds))
                return Math.Clamp(seconds, 3, 120);
            return 15;
        }

        private static bool Run(int seconds, string logPath)
        {
            var config = LoadConfig();
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            using var device = ResolveAudioEndpoint(config, devices);
            string wavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio_probe.wav");
            string mp4Path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audio_probe.mp4");
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
            try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }

            var log = new StringBuilder();
            log.AppendLine($"Audio probe started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.AppendLine($"DurationSeconds={seconds}");
            log.AppendLine($"ConfiguredName={config.AudioDeviceName}");
            log.AppendLine($"ConfiguredEndpoint={config.AudioDeviceMoniker}");
            log.AppendLine($"SelectedDevice={device.FriendlyName}");
            log.AppendLine($"SelectedEndpoint={device.ID}");

            int packets = 0;
            long bytes = 0;
            short peak = 0;
            int gapCount = 0;
            double maxGapMs = 0;
            DateTime lastPacketAt = DateTime.MinValue;
            using var capture = new WasapiCapture(device, true, 100)
            {
                ShareMode = AudioClientShareMode.Shared
            };
            var writer = new WaveFileWriter(wavPath, capture.WaveFormat);

            log.AppendLine($"SourceFormat={capture.WaveFormat}");
            log.AppendLine("WasapiEventSync=true");
            log.AppendLine("BufferMs=100");
            log.AppendLine($"WavPath={wavPath}");

            capture.DataAvailable += (_, e) =>
            {
                var now = DateTime.Now;
                if (lastPacketAt != DateTime.MinValue)
                {
                    double gapMs = (now - lastPacketAt).TotalMilliseconds;
                    if (gapMs > 750)
                    {
                        gapCount++;
                        if (gapMs > maxGapMs) maxGapMs = gapMs;
                    }
                }

                lastPacketAt = now;
                packets++;
                bytes += e.BytesRecorded;
                writer.Write(e.Buffer, 0, e.BytesRecorded);
                if (TryGetPeak(e.Buffer, e.BytesRecorded, capture.WaveFormat, out short packetPeak) && packetPeak > peak)
                    peak = packetPeak;
            };

            Exception? stoppedException = null;
            capture.RecordingStopped += (_, e) => stoppedException = e.Exception;

            capture.StartRecording();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            capture.StopRecording();
            writer.Flush();
            writer.Dispose();

            var wavInfo = ReadWavInfo(wavPath);
            var mp4Info = ProbeMp4Mux(wavPath, mp4Path, seconds);
            bool ok = stoppedException == null
                && packets > 0
                && bytes > 0
                && gapCount == 0
                && wavInfo.Valid
                && wavInfo.DurationSeconds >= seconds * 0.8
                && mp4Info.Valid;
            log.AppendLine($"Packets={packets}");
            log.AppendLine($"Bytes={bytes}");
            log.AppendLine($"Peak={peak}");
            log.AppendLine($"GapCount={gapCount}");
            log.AppendLine($"MaxGapMs={maxGapMs:F0}");
            log.AppendLine($"WavValid={wavInfo.Valid}");
            log.AppendLine($"WavBytes={wavInfo.FileBytes}");
            log.AppendLine($"WavDurationSeconds={wavInfo.DurationSeconds:F2}");
            log.AppendLine($"WavError={wavInfo.Error ?? "(none)"}");
            log.AppendLine($"Mp4Path={mp4Path}");
            log.AppendLine($"Mp4Valid={mp4Info.Valid}");
            log.AppendLine($"Mp4Bytes={mp4Info.FileBytes}");
            log.AppendLine($"Mp4AudioDecodeOk={mp4Info.AudioDecodeOk}");
            log.AppendLine($"Mp4Error={mp4Info.Error ?? "(none)"}");
            log.AppendLine($"StoppedException={stoppedException?.Message ?? "(none)"}");
            log.AppendLine($"Result={(ok ? "OK" : "FAILED")}");
            File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
            return ok;
        }

        private static (bool Valid, long FileBytes, bool AudioDecodeOk, string? Error) ProbeMp4Mux(string wavPath, string mp4Path, int seconds)
        {
            string? ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
                return (false, 0, false, "ffmpeg.exe not found.");

            string muxArgs = $"-y -f lavfi -i \"testsrc2=size=320x180:rate=10:duration={seconds}\" -i \"{wavPath}\" -map 0:v:0 -map 1:a:0 -c:v libx264 -preset ultrafast -crf 35 -c:a aac -b:a 128k -shortest \"{mp4Path}\"";
            var mux = RunProcess(ffmpegPath, muxArgs, Math.Max(15000, seconds * 3000));
            if (!mux.Exited || mux.ExitCode != 0 || !File.Exists(mp4Path) || new FileInfo(mp4Path).Length <= 0)
                return (false, File.Exists(mp4Path) ? new FileInfo(mp4Path).Length : 0, false, $"Mux failed: exited={mux.Exited}, exitCode={mux.ExitCode}, stderr={TrimForLog(mux.Stderr)}");

            string decodeArgs = $"-v error -i \"{mp4Path}\" -map 0:a:0 -f null NUL";
            var decode = RunProcess(ffmpegPath, decodeArgs, Math.Max(15000, seconds * 3000));
            bool decodeOk = decode.Exited && decode.ExitCode == 0;
            string? error = decodeOk ? null : $"Audio decode failed: exited={decode.Exited}, exitCode={decode.ExitCode}, stderr={TrimForLog(decode.Stderr)}";
            return (decodeOk, new FileInfo(mp4Path).Length, decodeOk, error);
        }

        private static string? FindFFmpeg()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            string projectLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "ffmpeg.exe");
            string fullProjectLocal = Path.GetFullPath(projectLocal);
            if (File.Exists(fullProjectLocal)) return fullProjectLocal;

            return null;
        }

        private static (bool Exited, int ExitCode, string Stderr) RunProcess(string fileName, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, -1, "Process failed to start.");

            string stderr = string.Empty;
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            bool exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                try { process.Kill(); } catch { }
                try { process.WaitForExit(3000); } catch { }
            }

            try { stderr = stderrTask.GetAwaiter().GetResult(); } catch { }
            try { _ = stdoutTask.GetAwaiter().GetResult(); } catch { }
            return (exited, exited ? process.ExitCode : -1, stderr);
        }

        private static string TrimForLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 500 ? text : text[..500];
        }

        private static (bool Valid, long FileBytes, double DurationSeconds, string? Error) ReadWavInfo(string wavPath)
        {
            try
            {
                if (!File.Exists(wavPath))
                    return (false, 0, 0, "File not found.");
                using var reader = new WaveFileReader(wavPath);
                return (reader.Length > 0, new FileInfo(wavPath).Length, reader.TotalTime.TotalSeconds, null);
            }
            catch (Exception ex)
            {
                return (false, File.Exists(wavPath) ? new FileInfo(wavPath).Length : 0, 0, ex.Message);
            }
        }

        private static AppConfig LoadConfig()
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (!File.Exists(configPath)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new AppConfig();
        }

        private static MMDevice ResolveAudioEndpoint(AppConfig config, MMDeviceCollection devices)
        {
            if (devices == null || devices.Count == 0)
                throw new InvalidOperationException("No active capture endpoint was found.");

            bool hasConfiguredEndpoint = false;
            if (!string.IsNullOrWhiteSpace(config.AudioDeviceMoniker))
            {
                hasConfiguredEndpoint = true;
                foreach (var device in devices)
                {
                    if (AudioEndpointMatches(device.ID, config.AudioDeviceMoniker))
                        return device;
                }
            }

            if (!string.IsNullOrWhiteSpace(config.AudioDeviceName))
            {
                hasConfiguredEndpoint = true;
                foreach (var device in devices)
                {
                    if (AudioEndpointMatches(device.FriendlyName, config.AudioDeviceName)
                        || AudioEndpointMatches(GetEndpointDisplayName(device), config.AudioDeviceName))
                        return device;
                }
            }

            if (hasConfiguredEndpoint)
                throw new InvalidOperationException("Configured microphone endpoint was not found.");

            using var enumerator = new MMDeviceEnumerator();
            try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
            catch { return devices[0]; }
        }

        private static string GetEndpointDisplayName(MMDevice device)
        {
            try { return device.DeviceFriendlyName; } catch { return device.FriendlyName; }
        }

        private static bool AudioEndpointMatches(string endpointName, string configuredName)
        {
            if (string.IsNullOrWhiteSpace(endpointName) || string.IsNullOrWhiteSpace(configuredName))
                return false;

            return endpointName.Equals(configuredName, StringComparison.OrdinalIgnoreCase)
                || endpointName.Contains(configuredName, StringComparison.OrdinalIgnoreCase)
                || configuredName.Contains(endpointName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetPeak(byte[] buffer, int bytesRecorded, WaveFormat format, out short peak)
        {
            peak = 0;
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                || (format.Encoding == WaveFormatEncoding.Extensible
                    && format is WaveFormatExtensible floatExtensible
                    && floatExtensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            bool isPcm = format.Encoding == WaveFormatEncoding.Pcm
                || (format.Encoding == WaveFormatEncoding.Extensible
                    && format is WaveFormatExtensible pcmExtensible
                    && pcmExtensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71"));

            if (isFloat && format.BitsPerSample == 32)
            {
                for (int i = 0; i + 3 < bytesRecorded; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer, i);
                    int scaled = (int)Math.Clamp(Math.Abs(sample) * short.MaxValue, 0, short.MaxValue);
                    if (scaled > peak) peak = (short)scaled;
                }
                return true;
            }

            if (!isPcm) return false;
            int bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
            for (int i = 0; i + bytesPerSample - 1 < bytesRecorded; i += bytesPerSample)
            {
                short sample = bytesPerSample switch
                {
                    1 => (short)((buffer[i] - 128) << 8),
                    2 => BitConverter.ToInt16(buffer, i),
                    3 => (short)(ReadInt24(buffer, i) >> 8),
                    4 => (short)(BitConverter.ToInt32(buffer, i) >> 16),
                    _ => 0
                };
                short abs = sample == short.MinValue ? short.MaxValue : (short)Math.Abs(sample);
                if (abs > peak) peak = abs;
            }
            return true;
        }

        private static int ReadInt24(byte[] buffer, int offset)
        {
            int value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((value & 0x800000) != 0)
                value |= unchecked((int)0xff000000);
            return value;
        }
    }
}
