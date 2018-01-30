using CommandLine;
using DokanNet;
using Renci.SshNet;
using Renci.SshNet.Common;
using Sshfs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommandLine.Text;
using DokanNet.Logging;

namespace SSHFS.CLI
{
    class Options
    {
        // Paths
        [Option('d', "drive-letter",
            Required = true,
            HelpText = "Drive letter to mount the remote SFTP path under")]
        public char DriveLetter { get; set; }

        [Option('r', "path",
            Required = true,
            HelpText = "Absolute path of directory to be mounted from remote system")]
        public string Path { get; set; }

        // Remote host
        [Option('h', "host",
            Required = true,
            HelpText = "IP or hostname of remote host")]
        public string Host { get; set; }

        [Option('p', "port",
            Required = false, Default = 22,
            HelpText = "SSH service port on remote server")]
        public int Port { get; set; }

        // Auth
        [Option('u', "username",
            Required = true,
            HelpText = "Name of SSH user on remote system")]
        public string Username { get; set; }

        [Option('x', "password",
            Required = false,
            HelpText = "Read password from stdin")]
        public bool Password { get; set; }

        [Option('k', "private-keys",
            Required = false,
            HelpText = "Path to SSH user's private key(s), if key-based auth should be attempted")]
        public IEnumerable<string> Keys { get; set; }

        // Logging
        [Option('v', "verbose",
            Required = false, Default = false,
            HelpText = "Enable Dokan logging from mounted filesystem")]
        public bool Logging { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(Start);
        }

        static void Start(Options options)
        {
            var auths = GetAuthMechanisms(options);

            var fs = auths
                .Select(auth => AttemptConnection(auth.Item1, auth.Item2, options.Path))
                .FirstOrDefault(result => result != null);

            if (fs == null)
                throw new InvalidOperationException(
                    "Could not connect to server with any known authentication mechanism");

            fs.Mount($"{options.DriveLetter}", options.Logging ? null : new NullLogger());
        }

        static IEnumerable<(string, ConnectionInfo)> GetAuthMechanisms(Options options)
        {
            var auths = new List<(string, ConnectionInfo)>();

            if (options.Keys != null && options.Keys.Any())
            {
                auths.AddRange(new(string, ConnectionInfo)[]
                {
                    ("private key", PrivateKeyConnectionInfo(options))
                });
            }
            else if (options.Password)
            {
                Console.WriteLine("No SSH key file selected, using password auth instead.");
                var pass = ReadPassword("Please enter password: ");

                auths.AddRange(new(string, ConnectionInfo)[]
                {
                    ("password", new PasswordConnectionInfo(options.Host, options.Port, options.Username, pass)),
                    ("keyboard-interactive", KeyboardInteractiveConnectionInfo(options, pass))
                });
            }
            else
            {
                Console.WriteLine(
                    "No key files specified, and password auth not enabled (win-sshfs does not search for private keys). Aborting...");
                Environment.Exit(1);
            }

            return auths;
        }

        static PrivateKeyConnectionInfo PrivateKeyConnectionInfo(Options options)
        {
            var pkFiles = options.Keys.Select(k =>
                options.Password
                    ? new PrivateKeyFile(k, ReadPassword($"Enter passphrase for {k}: "))
                    : new PrivateKeyFile(k));

            return new PrivateKeyConnectionInfo(options.Host, options.Port, options.Username, pkFiles.ToArray());
        }

        static string ReadPassword(string prompt)
        {
            if (!Console.IsInputRedirected)
                return ReadLine.ReadPassword(prompt);

            Console.WriteLine(prompt);
            return Console.ReadLine();
        }

        static KeyboardInteractiveConnectionInfo KeyboardInteractiveConnectionInfo(Options options, string pass)
        {
            var auth = new KeyboardInteractiveConnectionInfo(options.Host, options.Port, options.Username);

            auth.AuthenticationPrompt += (sender, e) =>
            {
                var passPrompts = e.Prompts
                    .Where(p => p.Request.StartsWith("Password:"));
                foreach (var p in passPrompts)
                    p.Response = pass;
            };

            return auth;
        }

        static SftpFilesystem AttemptConnection(string authType, ConnectionInfo connInfo, string path)
        {
            try
            {
                var sftpFS = new SftpFilesystem(connInfo, path);
                sftpFS.Connect();
                Console.WriteLine($"Successfully authenticated with {authType}.");
                return sftpFS;
            }
            catch (SshAuthenticationException)
            {
                Console.WriteLine(
                    $"Failed to authenticate using {authType}, falling back to next auth mechanism if available.");
                return null;
            }
        }
    }
}