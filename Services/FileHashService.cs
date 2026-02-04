using System;
using System.Security.Cryptography;
using System.IO;
using System.Threading.Tasks;
using System.IO.MemoryMappedFiles;

namespace DupFree.Services
{
    public static class FileHashService
    {
        public static async Task<string> GetFileHashAsync(string filePath)
        {
            try
            {
                // Use memory-mapped file I/O for full hash - 2-3x faster than streams
                var result = await Task.Run(() =>
                {
                    try
                    {
                        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                        using var accessor = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
                        using var sha256 = SHA256.Create();
                        var hash = sha256.ComputeHash(accessor);
                        return Convert.ToHexString(hash);
                    }
                    catch
                    {
                        // Fallback to stream I/O if memory mapping fails
                        using var sha256 = SHA256.Create();
                        using var fs = File.OpenRead(filePath);
                        var hash = sha256.ComputeHash(fs);
                        return Convert.ToHexString(hash);
                    }
                });

                return result;
            }
            catch
            {
                return null;
            }
        }

        public static async Task<string> GetQuickHashAsync(string filePath)
        {
            try
            {
                // Ultra-fast quick hash - only 64KB from start for WizTree-speed detection
                var result = await Task.Run(() =>
                {
                    try
                    {
                        const long sampleSize = 65536; // 64KB from start - maximum speed
                        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                        using var accessor = mmf.CreateViewStream(0, Math.Min(sampleSize, new FileInfo(filePath).Length), MemoryMappedFileAccess.Read);
                        using var sha256 = SHA256.Create();
                        
                        var buffer = new byte[Math.Min((int)sampleSize, 32768)];
                        using var ms = new MemoryStream();
                        int read;
                        long totalRead = 0;
                        while ((read = accessor.Read(buffer, 0, buffer.Length)) > 0 && totalRead < sampleSize)
                        {
                            int toWrite = (int)Math.Min(read, sampleSize - totalRead);
                            ms.Write(buffer, 0, toWrite);
                            totalRead += toWrite;
                        }
                        
                        ms.Seek(0, SeekOrigin.Begin);
                        var hash = sha256.ComputeHash(ms);
                        return Convert.ToHexString(hash);
                    }
                    catch
                    {
                        // Fallback: direct file read without MMF
                        using var fs = File.OpenRead(filePath);
                        using var sha256 = SHA256.Create();
                        
                        const int sampleSize = 65536; // 64KB
                        var buffer = new byte[Math.Min(sampleSize, (int)fs.Length)];
                        int read = fs.Read(buffer, 0, buffer.Length);
                        
                        if (read < buffer.Length)
                            Array.Resize(ref buffer, read);
                        
                        var hash = sha256.ComputeHash(buffer);
                        return Convert.ToHexString(hash);
                    }
                });

                return result;
            }
            catch
            {
                return null;
            }
        }

    }
}
