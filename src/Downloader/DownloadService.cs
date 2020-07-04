﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Downloader
{
    public partial class DownloadService : IDownloadService
    {
        public DownloadService(DownloadConfiguration options = null)
        {
            Package = new DownloadPackage()
            {
                Options = options ?? new DownloadConfiguration()
            };

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = false; // accept the request for POST, PUT and PATCH verbs
            ServicePointManager.DefaultConnectionLimit = 1000;
            ServicePointManager.MaxServicePointIdleTime = 1000;
        }



        protected long BytesReceivedCheckPoint { get; set; }
        protected long LastDownloadCheckpoint { get; set; }
        protected bool RemoveTempsAfterDownloadCompleted { get; set; } = true;
        protected CancellationTokenSource Cts { get; set; }

        /// <summary>
        /// Is in downloading time
        /// </summary>
        public bool IsBusy { get; protected set; }
        public string MainProgressName { get; } = "Main";
        public long DownloadSpeed { get; set; }
        public DownloadPackage Package { get; set; }
        public event EventHandler<AsyncCompletedEventArgs> DownloadFileCompleted;
        public event EventHandler<DownloadProgressChangedEventArgs> DownloadProgressChanged;
        public event EventHandler<DownloadProgressChangedEventArgs> ChunkDownloadProgressChanged;

        public async Task DownloadFileAsync(DownloadPackage package)
        {
            IsBusy = true;
            Cts = new CancellationTokenSource();
            Package = package;
            Package.Options.Validate();

            if (Package.TotalFileSize <= 0)
                throw new InvalidDataException("File size is invalid!");

            if (File.Exists(Package.FileName))
                File.Delete(Package.FileName);

            await StartDownload();
        }
        public async Task DownloadFileAsync(string address, string fileName)
        {
            IsBusy = true;
            Cts = new CancellationTokenSource();
            Package.FileName = fileName;
            Package.Address = new Uri(address);
            Package.TotalFileSize = GetFileSize(Package.Address);
            Package.Options.Validate();

            if (Package.TotalFileSize <= 0)
                throw new InvalidDataException("File size is invalid!");

            var neededParts =
                (int)Math.Ceiling((double)Package.TotalFileSize / int.MaxValue); // for files as larger than 2GB

            // Handle number of parallel downloads  
            var parts = Package.Options.ChunkCount < neededParts ? neededParts : Package.Options.ChunkCount;

            Package.Chunks = ChunkFile(Package.TotalFileSize, parts);

            if (File.Exists(Package.FileName))
                File.Delete(Package.FileName);

            await StartDownload();
        }
        public void CancelAsync()
        {
            Cts?.Cancel(false);
        }
        public void Clear()
        {
            Cts?.Dispose();
            Cts = new CancellationTokenSource();
            Package.FileName = null;
            Package.TotalFileSize = 0;
            Package.BytesReceived = 0;
            Package.Chunks = null;
        }

        protected HttpWebRequest GetRequest(string method, Uri address)
        {
            var request = (HttpWebRequest)WebRequest.Create(address);
            request.Timeout = -1;
            request.Method = method;

            request.Accept = Package.Options.RequestConfiguration.Accept;
            request.KeepAlive = Package.Options.RequestConfiguration.KeepAlive;
            request.AllowAutoRedirect = Package.Options.RequestConfiguration.AllowAutoRedirect;
            request.AutomaticDecompression = Package.Options.RequestConfiguration.AutomaticDecompression;
            request.UserAgent = Package.Options.RequestConfiguration.UserAgent;
            request.ProtocolVersion = Package.Options.RequestConfiguration.ProtocolVersion;
            request.UseDefaultCredentials = Package.Options.RequestConfiguration.UseDefaultCredentials;
            request.SendChunked = Package.Options.RequestConfiguration.SendChunked;
            request.TransferEncoding = Package.Options.RequestConfiguration.TransferEncoding;
            request.Expect = Package.Options.RequestConfiguration.Expect;
            request.MaximumAutomaticRedirections = Package.Options.RequestConfiguration.MaximumAutomaticRedirections;
            request.MediaType = Package.Options.RequestConfiguration.MediaType;
            request.PreAuthenticate = Package.Options.RequestConfiguration.PreAuthenticate;
            request.Credentials = Package.Options.RequestConfiguration.Credentials;
            request.ClientCertificates = Package.Options.RequestConfiguration.ClientCertificates;
            request.Referer = Package.Options.RequestConfiguration.Referer;
            request.Pipelined = Package.Options.RequestConfiguration.Pipelined;
            request.Proxy = Package.Options.RequestConfiguration.Proxy;

            if (Package.Options.RequestConfiguration.IfModifiedSince.HasValue)
                request.IfModifiedSince = Package.Options.RequestConfiguration.IfModifiedSince.Value;

            return request;
        }
        protected long GetFileSize(Uri address)
        {
            var request = GetRequest("HEAD", address);
            using var response = request.GetResponse();
            // if (long.TryParse(webResponse.Headers.Get("Content-Length"), out var respLength))
            return response.ContentLength;
        }
        protected Chunk[] ChunkFile(long fileSize, int parts)
        {
            if (parts < 1)
                parts = 1;

            var chunkSize = fileSize / parts;

            if (chunkSize < 1)
            {
                chunkSize = 1;
                parts = (int)fileSize;
            }

            var chunks = new Chunk[parts];
            for (var chunk = 0; chunk < parts; chunk++)
            {
                chunks[chunk] =
                    (chunk == parts - 1)
                        ? new Chunk(chunk * chunkSize, fileSize - 1) // last chunk
                        : new Chunk(chunk * chunkSize, (chunk + 1) * chunkSize - 1);
            }

            return chunks;
        }
        protected async Task StartDownload()
        {
            try
            {
                var cancellationToken = Cts.Token;
                var tasks = new List<Task>();
                foreach (var chunk in Package.Chunks)
                {
                    if (Package.Options.ParallelDownload)
                    {   // download as parallel
                        var task = DownloadChunk(Package.Address, chunk, cancellationToken);
                        tasks.Add(task);
                    }
                    else
                    {   // download as async and serial
                        await DownloadChunk(Package.Address, chunk, cancellationToken);
                    }
                }

                if (Package.Options.ParallelDownload && cancellationToken.IsCancellationRequested == false) // is parallel
                    Task.WaitAll(tasks.ToArray(), cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    OnDownloadFileCompleted(new AsyncCompletedEventArgs(null, true, Package));
                    return;
                }

                // Merge data to single file
                await MergeChunks(Package.Chunks);

                OnDownloadFileCompleted(new AsyncCompletedEventArgs(null, false, Package));
            }
            finally
            {
                if (Cts.Token.IsCancellationRequested == false)
                {
                    // remove temp files
                    RemoveTemps();
                }
            }
        }
        protected async Task<Chunk> DownloadChunk(Uri address, Chunk chunk, CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested)
                    return chunk;

                var request = GetRequest("GET", address);
                if (chunk.Start + chunk.Position >= chunk.End)
                    return chunk; // downloaded completely before
                request.AddRange(chunk.Start + chunk.Position, chunk.End);

                using var httpWebResponse = request.GetResponse() as HttpWebResponse;
                if (httpWebResponse == null)
                    return chunk;

                var stream = httpWebResponse.GetResponseStream();
                var destinationStream = new ThrottledStream(stream, Package.Options.MaximumBytesPerSecond / Package.Options.ChunkCount);
                using (stream)
                {
                    if (stream == null)
                        return chunk;

                    if (Package.Options.OnTheFlyDownload)
                        await ReadStreamOnTheFly(destinationStream, chunk, token);
                    else
                        await ReadStreamOnTheFile(destinationStream, chunk, token);
                }

                return chunk;
            }
            catch (TaskCanceledException) // when stream reader timeout occured 
            {
                // re-request
                if (token.IsCancellationRequested == false)
                    await DownloadChunk(address, chunk, token);
            }
            catch (WebException) when (token.IsCancellationRequested == false &&
                                       chunk.FailoverCount++ <= Package.Options.MaxTryAgainOnFailover) // when the host forcibly closed the connection.
            {
                await Task.Delay(Package.Options.Timeout, token);
                chunk.Checkpoint();
                // re-request
                await DownloadChunk(address, chunk, token);
            }
            catch (Exception e) when (token.IsCancellationRequested == false &&
                                     chunk.FailoverCount++ <= Package.Options.MaxTryAgainOnFailover &&
                                     (e.Source == "System.Net.Http" ||
                                      e.Source == "System.Net.Sockets" ||
                                      e.Source == "System.Net.Security" ||
                                      e.InnerException is SocketException))
            {
                // wait and decrease speed to low pressure on host
                Package.Options.Timeout += chunk.CanContinue() ? 0 : 500;
                chunk.Checkpoint();
                await Task.Delay(Package.Options.Timeout, token);
                // re-request
                await DownloadChunk(address, chunk, token);
            }
            catch (Exception e) // Maybe no internet!
            {
                OnDownloadFileCompleted(new AsyncCompletedEventArgs(e, false, Package));
                Debugger.Break();
            }

            return chunk;
        }

        protected async Task ReadStreamOnTheFly(Stream stream, Chunk chunk, CancellationToken token)
        {
            var bytesToReceiveCount = chunk.Length - chunk.Position;
            chunk.Data ??= new byte[chunk.Length];
            while (bytesToReceiveCount > 0)
            {
                if (token.IsCancellationRequested)
                    return;

                using var innerCts = new CancellationTokenSource(Package.Options.Timeout);
                var count = bytesToReceiveCount > Package.Options.BufferBlockSize
                    ? Package.Options.BufferBlockSize
                    : (int)bytesToReceiveCount;
                var readSize = await stream.ReadAsync(chunk.Data, chunk.Position, count, innerCts.Token);
                Package.BytesReceived += readSize;
                chunk.Position += readSize;
                bytesToReceiveCount = chunk.Length - chunk.Position;

                OnChunkDownloadProgressChanged(new DownloadProgressChangedEventArgs(chunk.Id.ToString(), chunk.Length, chunk.Position, DownloadSpeed));
                OnDownloadProgressChanged(new DownloadProgressChangedEventArgs(MainProgressName, Package.TotalFileSize, Package.BytesReceived, DownloadSpeed));
            }
        }
        protected async Task ReadStreamOnTheFile(Stream stream, Chunk chunk, CancellationToken token)
        {
            var bytesToReceiveCount = chunk.Length - chunk.Position;
            if (string.IsNullOrWhiteSpace(chunk.FileName) || File.Exists(chunk.FileName) == false)
                chunk.FileName = Path.GetTempFileName();

            using var writer = new FileStream(chunk.FileName, FileMode.Append, FileAccess.Write, FileShare.Delete);
            while (bytesToReceiveCount > 0)
            {
                if (token.IsCancellationRequested)
                    return;

                using var innerCts = new CancellationTokenSource(Package.Options.Timeout);
                var count = bytesToReceiveCount > Package.Options.BufferBlockSize
                    ? Package.Options.BufferBlockSize
                    : (int)bytesToReceiveCount;
                var buffer = new byte[count];
                var readSize = await stream.ReadAsync(buffer, 0, count, innerCts.Token);
                // ReSharper disable once MethodSupportsCancellation
                await writer.WriteAsync(buffer, 0, readSize);
                Package.BytesReceived += readSize;
                chunk.Position += readSize;
                bytesToReceiveCount = chunk.Length - chunk.Position;

                OnChunkDownloadProgressChanged(new DownloadProgressChangedEventArgs(chunk.Id.ToString(), chunk.Length, chunk.Position, DownloadSpeed));
                OnDownloadProgressChanged(new DownloadProgressChangedEventArgs(MainProgressName, Package.TotalFileSize, Package.BytesReceived, DownloadSpeed));
            }
        }
        protected async Task MergeChunks(Chunk[] chunks)
        {
            var directory = Path.GetDirectoryName(Package.FileName);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            if (Directory.Exists(directory) == false)
                Directory.CreateDirectory(directory);

            using var destinationStream = new FileStream(Package.FileName, FileMode.Append, FileAccess.Write);
            foreach (var chunk in chunks.OrderBy(c => c.Start))
            {
                if (Package.Options.OnTheFlyDownload)
                    await destinationStream.WriteAsync(chunk.Data, 0, (int)chunk.Length);
                else if (File.Exists(chunk.FileName))
                {
                    using var reader = File.OpenRead(chunk.FileName);
                    await reader.CopyToAsync(destinationStream);
                }
            }
        }
        protected void RemoveTemps()
        {
            if (RemoveTempsAfterDownloadCompleted)
            {
                foreach (var chunk in Package.Chunks)
                {
                    if (Package.Options.OnTheFlyDownload)
                        chunk.Data = null;
                    else if (File.Exists(chunk.FileName))
                        File.Delete(chunk.FileName);
                }
                GC.Collect();
            }
        }

        protected virtual void OnDownloadFileCompleted(AsyncCompletedEventArgs e)
        {
            IsBusy = false;
            DownloadFileCompleted?.Invoke(this, e);
        }
        protected virtual void OnDownloadProgressChanged(DownloadProgressChangedEventArgs e)
        {
            OnDownloadSpeedCalculator();
            DownloadProgressChanged?.Invoke(this, e);
        }
        protected virtual void OnChunkDownloadProgressChanged(DownloadProgressChangedEventArgs e)
        {
            ChunkDownloadProgressChanged?.Invoke(this, e);
        }
        protected virtual void OnDownloadSpeedCalculator()
        {
            // calc download speed
            var timeDiff = Environment.TickCount - LastDownloadCheckpoint + 1;
            if (timeDiff < 1000) return;
            var bytesDiff = Package.BytesReceived - BytesReceivedCheckPoint;
            DownloadSpeed = bytesDiff * 1000 / timeDiff;
            LastDownloadCheckpoint = Environment.TickCount;
            BytesReceivedCheckPoint = Package.BytesReceived;
        }
    }
}