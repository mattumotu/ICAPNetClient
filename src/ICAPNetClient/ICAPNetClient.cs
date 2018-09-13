﻿namespace ICAPNetClient
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;

    public class ICAPNetClient : IDisposable
    {
        private const string VERSION = "1.0";
        private const string USERAGENT = "IT-Kartellet ICAP Client/1.1";
        private const string ICAPTERMINATOR = "\r\n\r\n";
        private const string HTTPTERMINATOR = "0\r\n\r\n";
        private const int StdRecieveLength = 8192;
        private const int StdSendLength = 8192;

        private readonly string serverIP;
        private readonly int port;
        private readonly string icapService;
        private readonly int stdPreviewSize;
        private readonly byte[] buffer = new byte[8192];

        private Socket sender;
        private string tempString;

        /// <summary>
        /// Initializes a new instance of the <see cref="ICAPNetClient" /> class.
        /// Initializes the socket connection and IO streams. It askes the server for the available options and
        /// changes settings to match it.
        /// </summary>
        /// <param name="serverIP">The IP address to connect to.</param>
        /// <param name="port">The port in the host to use.</param>
        /// <param name="icapService">The service to use.</param>
        /// <param name="previewSize">Specify a preview size to overwrite server preferences</param>
        /// <exception cref="ICAPException">Thrown when error occurs in communication with server</exception>
        /// <exception cref="SocketException">Thrown when error occurs in connection to server</exception>
        public ICAPNetClient(string serverIP, int port, string icapService, int previewSize = -1)
        {
            this.serverIP = serverIP;
            this.port = port;
            this.icapService = icapService;

            // Initialize connection
            IPAddress ipAddress = IPAddress.Parse(serverIP);
            IPEndPoint remoteEP = new IPEndPoint(ipAddress, port);

            // Create a TCP/IP  socket.
            sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sender.Connect(remoteEP);

            if (previewSize != -1)
            {
                stdPreviewSize = previewSize;
            }
            else
            {
                string parseMe = GetOptions();
                Dictionary<string, string> responseMap = ParseHeader(parseMe);

                responseMap.TryGetValue("StatusCode", out tempString);
                if (tempString != null)
                {
                    int status = Convert.ToInt16(tempString);

                    switch (status)
                    {
                        case 200:
                            responseMap.TryGetValue("Preview", out tempString);
                            if (tempString != null)
                            {
                                stdPreviewSize = Convert.ToInt16(tempString);
                            }

                            break;

                        default: throw new ICAPException("Could not get preview size from server");
                    }
                }
                else
                {
                    throw new ICAPException("Could not get options from server");
                }
            }
        }

        /// <summary>
        /// Automatically asks for the servers available options and returns the raw response as a string.
        /// </summary>
        /// <param name="filepath">Relative or absolute path to a file.</param>
        /// <exception cref="ICAPException">Thrown when error occurs in communication with server</exception>
        /// <exception cref="IOException">Thrown when error occurs in reading file</exception>
        /// <exception cref="SocketException">Thrown if socket is closed unexpectedly.</exception>
        /// <returns></returns>
        public bool ScanFile(string filepath)
        {
            using (FileStream fileStream = new FileStream(filepath, FileMode.Open))
            {
                int fileSize = (int)fileStream.Length;

                // First part of header
                string resBody = "Content-Length: " + fileSize + "\r\n\r\n";

                int previewSize = stdPreviewSize;
                if (fileSize < stdPreviewSize)
                {
                    previewSize = fileSize;
                }

                byte[] requestBuffer = Encoding.ASCII.GetBytes(
                    "RESPMOD icap://" + serverIP + "/" + icapService + " ICAP/" + VERSION + "\r\n"
                    + "Host: " + serverIP + "\r\n"
                    + "User-Agent: " + USERAGENT + "\r\n"
                    + "Allow: 204\r\n"
                    + "Preview: " + previewSize + "\r\n"
                    + "Encapsulated: res-hdr=0, res-body=" + resBody.Length + "\r\n"
                    + "\r\n"
                    + resBody
                    + previewSize.ToString("X") + "\r\n");

                sender.Send(requestBuffer);

                // Sending preview or, if smaller than previewSize, the whole file.
                byte[] chunk = new byte[previewSize];

                fileStream.Read(chunk, 0, previewSize);
                sender.Send(chunk);
                sender.Send(Encoding.ASCII.GetBytes("\r\n"));
                if (fileSize <= previewSize)
                {
                    sender.Send(Encoding.ASCII.GetBytes("0; ieof\r\n\r\n"));
                }
                else if (previewSize != 0)
                {
                    sender.Send(Encoding.ASCII.GetBytes("0\r\n\r\n"));
                }

                // Parse the response! It might not be "100 continue".
                // if fileSize<previewSize, then the stream waiting is actually the allowed/disallowed signal
                // otherwise it is a "go" for the rest of the file.
                Dictionary<string, string> responseMap = new Dictionary<string, string>();
                int status;

                if (fileSize > previewSize)
                {
                    // TODO: add timeout. It will hang if no response is recieved
                    string parseMe = this.GetHeader(ICAPTERMINATOR);
                    responseMap = ParseHeader(parseMe);

                    responseMap.TryGetValue("StatusCode", out tempString);
                    if (tempString != null)
                    {
                        status = Convert.ToInt16(tempString);

                        switch (status)
                        {
                            case 100: break; // Continue transfer
                            case 200: return false;
                            case 204: return true;
                            case 404: throw new ICAPException("404: ICAP Service not found");
                            default: throw new ICAPException("Server returned unknown status code:" + status);
                        }
                    }
                }

                // Sending remaining part of file
                if (fileSize > previewSize)
                {
                    int offset = previewSize;
                    int n;
                    byte[] buffer = new byte[StdSendLength];
                    while ((n = fileStream.Read(buffer, 0, StdSendLength)) > 0)
                    {
                        offset += n;  // offset for next reading
                        sender.Send(Encoding.ASCII.GetBytes(buffer.Length.ToString("X") + "\r\n"));
                        sender.Send(buffer);
                        sender.Send(Encoding.ASCII.GetBytes("\r\n"));
                    }

                    // Closing file transfer.
                    sender.Send(Encoding.ASCII.GetBytes("0\r\n\r\n"));
                }
                ////fileStream.Close();

                responseMap.Clear();
                string response = GetHeader(ICAPTERMINATOR);
                responseMap = ParseHeader(response);

                responseMap.TryGetValue("StatusCode", out tempString);
                if (tempString != null)
                {
                    status = Convert.ToInt16(tempString);

                    if (status == 204)
                    {
                        // Unmodified
                        return true;
                    }

                    if (status == 200)
                    {
                        // OK - The ICAP status is ok, but the encapsulated HTTP status will likely be different
                        response = GetHeader(HTTPTERMINATOR);

                        // Searching for: <title>ProxyAV: Access Denied</title>
                        int x = response.IndexOf("<title>", 0);
                        int y = response.IndexOf("</title>", x);
                        string statusCode = response.Substring(x + 7, y - x - 7);

                        if (statusCode.Equals("ProxyAV: Access Denied"))
                        {
                            return false;
                        }
                    }
                }
                throw new ICAPException("Unrecognized or no status code in response header.");
            }
        }

        public void Dispose()
        {
            sender.Shutdown(SocketShutdown.Both);
            sender.Close();
            ////fileStream.Close();
        }

        /// <summary>
        /// Automatically asks for the servers available options and returns the raw response as a string.
        /// </summary>
        /// <returns>string of the raw response</returns>
        private string GetOptions()
        {
            byte[] msg = Encoding.ASCII.GetBytes(
                "OPTIONS icap://" + serverIP + "/" + icapService + " ICAP/" + VERSION + "\r\n"
                + "Host: " + serverIP + "\r\n"
                + "User-Agent: " + USERAGENT + "\r\n"
                + "Encapsulated: null-body=0\r\n"
                + "\r\n");
            sender.Send(msg);

            return GetHeader(ICAPTERMINATOR);
        }

        /// <summary>
        /// Receive an expected ICAP header as response of a request. The returned string should be parsed with parseHeader()
        /// </summary>
        /// <param name="terminator">Relative or absolute path to a file.</param>
        /// <exception cref="ICAPException">Thrown when error occurs in communication with server</exception>
        /// <returns>string of the raw response</returns>
        private string GetHeader(string terminator)
        {
            byte[] endofheader = System.Text.Encoding.UTF8.GetBytes(terminator);
            byte[] buffer = new byte[StdRecieveLength];

            int n;
            int offset = 0;

            // stdRecieveLength-offset is replaced by '1' to not receive the next (HTTP) header.
            // first part is to secure against DOS
            while ((offset < StdRecieveLength) && ((n = sender.Receive(buffer, offset, 1, SocketFlags.None)) != 0))
            {
                offset += n;

                // 13 is the smallest possible message (ICAP/1.0 xxx\r\n) or (HTTP/1.0 xxx\r\n)
                if (offset > endofheader.Length + 13)
                {
                    byte[] lastBytes = new byte[endofheader.Length];
                    Array.Copy(buffer, offset - endofheader.Length, lastBytes, 0, endofheader.Length);
                    if (endofheader.SequenceEqual(lastBytes))
                    {
                        return Encoding.ASCII.GetString(buffer, 0, offset);
                    }
                }
            }
            throw new ICAPException("Error in getHeader() method");
        }

        /// <summary>
        /// Given a raw response header as a string, it will parse through it and return a Dictionary of the result
        /// </summary>
        /// <param name="response">A raw response header as a string.</param>
        /// <returns>Dictionary of the key,value pairs of the response</returns>
        private Dictionary<string, string> ParseHeader(string response)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();

            /****SAMPLE:****
             * ICAP/1.0 204 Unmodified
             * Server: C-ICAP/0.1.6
             * Connection: keep-alive
             * ISTag: CI0001-000-0978-6918203
             */

            // The status code is located between the first 2 whitespaces.
            // Read status code
            int x = response.IndexOf(" ", 0);
            int y = response.IndexOf(" ", x + 1);
            string statusCode = response.Substring(x + 1, y - x - 1);
            headers.Add("StatusCode", statusCode);

            // Each line in the sample is ended with "\r\n".
            // When (i+2==response.length()) The end of the header have been reached.
            // The +=2 is added to skip the "\r\n".
            // Read headers
            int i = response.IndexOf("\r\n", y);
            i += 2;
            while (i + 2 != response.Length && response.Substring(i).Contains(':'))
            {
                int n = response.IndexOf(":", i);
                string key = response.Substring(i, n - i);

                n += 2;
                i = response.IndexOf("\r\n", n);
                string value = response.Substring(n, i - n);

                headers.Add(key, value);
                i += 2;
            }
            return headers;
        }
    }
}