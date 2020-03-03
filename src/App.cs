using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.CommandLine;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.CommandLine.Invocation;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using static System.Console;

namespace Alelo.Console
{
    internal static class App
    {
        private static async Task<int> Main(string[] args)
        {
            var aleloHome = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALELO_HOME"))
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".alelo")
                : Environment.GetEnvironmentVariable("ALELO_HOME");

            var aleloDefaultProfile = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALELO_DEFAULT_PROFILE"))
                ? GetProfilesNames(false).FirstOrDefault()
                : Environment.GetEnvironmentVariable("ALELO_DEFAULT_PROFILE");

            var aleloDefaultCard = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALELO_DEFAULT_CARD"))
                ? string.Empty
                : Environment.GetEnvironmentVariable("ALELO_DEFAULT_CARD");

            var globalVerbose = false;

            var client = new HttpClient(new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false
            });

            if (!Directory.Exists(aleloHome))
                Directory.CreateDirectory(aleloHome);

            #region Profile management

            async Task AuthenticateProfile(string profileName)
            {
                if (string.IsNullOrEmpty(profileName.Trim()))
                {
                    WriteLine("[!] Invalid profile name!");
                    Environment.Exit(1);
                }

                if (!GetProfilesNames(false).Contains(profileName))
                {
                    WriteLine("[!] Profile name not found!");
                    Environment.Exit(1);
                }

                profileName = profileName.Trim();

                var profiles = await GetProfiles();
                var profile = profiles.FirstOrDefault(p => p.Name == profileName);

                if (profile is null || string.IsNullOrEmpty(profile.Name))
                {
                    WriteLine($"[!] Unable to deserialize the profile, delete {profileName} and create again!");
                    Environment.Exit(1);
                }

                if (string.IsNullOrEmpty(profile.Session.Token))
                {
                    WriteLine("[!] Session is not created, creating a new one...");

                    Write("[+] Profile CPF (just digits): ");
                    var cpf = ReadLine();

                    Write("[+] Password: ");

                    var password = string.Empty;
                    var nextKey = ReadKey(true);

                    while (nextKey.Key != ConsoleKey.Enter)
                    {
                        if (nextKey.Key == ConsoleKey.Backspace)
                        {
                            if (password.Length > 0)
                            {
                                password.Remove(password.Length - 1);
                            }
                        }
                        else
                        {
                            password += nextKey.KeyChar.ToString();
                        }
                        nextKey = ReadKey(true);
                    }
                    
                    WriteLine();

                    var response = await client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Post,
                            "https://www.meualelo.com.br/api/meualelo-web-api/s/p/authentication/login")
                        {
                            Headers =
                            {
                                {"X-api-key", GetAleloGatewayToken()},
                                {
                                    "User-Agent",
                                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:72.0) Gecko/20100101 Firefox/72.0"
                                }
                            },
                            Content = new StringContent($"{{\"cpf\":\"{cpf}\",\"password\":\"{password}\"}}",
                                Encoding.UTF8, "application/json")
                        });

                    if (!response.IsSuccessStatusCode)
                    {
                        WriteLine("[!] Invalid CPF/password!");
                        Environment.Exit(1);
                    }

                    var content = await response.Content.ReadAsStreamAsync();
                    var session = await JsonSerializer.DeserializeAsync<Session>(content);

                    if (string.IsNullOrEmpty(session.Token))
                    {
                        WriteLine("[!] Something bad happened, check the repository for updates!");
                        Environment.Exit(1);
                    }

                    WriteLine($"[+] {session.FullName} authenticated on profile {aleloDefaultProfile}");

                    var applicationProfiles = await GetProfiles();
                    var currentProfile = applicationProfiles.First(p => p.Name == aleloDefaultProfile);

                    currentProfile.Session = session;

                    WriteLine("[+] Saving current token to profile");

                    try
                    {
                        if (File.Exists(Path.Combine(aleloHome, profileName + ".json"))) 
                            File.Delete(Path.Combine(aleloHome, profileName + ".json"));

                        await using var fs = File.Create(Path.Combine(aleloHome, profileName + ".json"));
                        await JsonSerializer.SerializeAsync(fs, currentProfile);
                    }
                    catch (IOException err)
                    {
                        WriteLine("[!] Error when creating the profile file!");
                        WriteLine($" > {err.Message}");

                        Environment.Exit(1);
                    }
                    catch (Exception err)
                    {
                        WriteLine("[!] Unknown error happened!");
                        WriteLine($" > {err.Message}");

                        Environment.Exit(1);
                    }

                    WriteLine("[+] Profile authenticated and good to go!");
                }
            }

            async Task CreateProfile(string profileName)
            {
                if (string.IsNullOrEmpty(profileName.Trim()))
                {
                    WriteLine("[!] Invalid profile name!");
                    Environment.Exit(1);
                }

                profileName = profileName.Trim();

                if (GetProfilesNames(false).Contains(profileName))
                {
                    WriteLine("[!] Profile name already in use!");
                    Environment.Exit(1);
                }

                try
                {
                    await using var fs = File.Create(Path.Combine(aleloHome, profileName + ".json"));
                    await JsonSerializer.SerializeAsync(fs, new Profile
                    {
                        Name = profileName,
                        Session = new Session()
                    });
                }
                catch (IOException err)
                {
                    WriteLine("[!] Error when creating the profile file!");
                    WriteLine($" > {err.Message}");

                    Environment.Exit(1);
                }
                catch (Exception err)
                {
                    WriteLine("[!] Unknown error happened!");
                    WriteLine($" > {err.Message}");

                    Environment.Exit(1);
                }

                WriteLine($"[+] Profile {profileName} created under current ALELO_HOME ({aleloHome})");
            }

            #endregion

            #region Application commands

            Command Profile()
            {
                var profileCommand = new Command("profile",
                    "Select default, create, delete and list user profiles for Meu Alelo")
                {
                    new Option<bool>(new[] {"--list", "-l"})
                        {Description = $"List available profiles under current PULGA_HOME ({aleloHome})"},

                    new Option<string>(new[] {"--create", "-c"})
                    {
                        Description = $"Create new profiles under current PULGA_HOME ({aleloHome})",
                        Argument = new Argument<string>
                            {Arity = ArgumentArity.ExactlyOne, Name = "profile name", Description = "Name to new profile"}
                    },

                    new Option<string>(new[] {"--delete", "-d"})
                    {
                        Description = $"Delete a profiles under current PULGA_HOME ({aleloHome})",
                        Argument = new Argument<string>
                        {
                            Arity = ArgumentArity.ExactlyOne, Name = "profile name",
                            Description = "Name of the profile to delete"
                        }
                    },

                    new Option<string>(new[] {"--profile", "-p"})
                    {
                        Description =
                            $"Select a default profile for this session under current PULGA_HOME ({aleloHome})",
                        Argument = new Argument<string>
                        {
                            Arity = ArgumentArity.ExactlyOne, Name = "profile name",
                            Description = "Name of the profile to use"
                        }
                    },

                    new Option<string>(new[] {"--authenticate", "-a"})
                    {
                        Description =
                            $"Authenticate or refresh session for specified profile",
                        Argument = new Argument<string>
                        {
                            Arity = ArgumentArity.ExactlyOne, Name = "profile name",
                            Description = "Name of the profile to use"
                        }
                    },

                    new Option<bool>(new[] {"--current-profile"}) {Description = "Profile used by default"}
                };

                profileCommand.Handler =
                    CommandHandler.Create<bool, string, string, string, bool, string>(async (list, create, delete, profile,
                        currentProfile, authenticate) =>
                    {
                        if (currentProfile)
                            WriteLine($"[+] Current profile is {(string.IsNullOrEmpty(aleloDefaultProfile) ? "No profiles created" : aleloDefaultProfile)}");

                        if (list)
                        {
                            WriteLine("[+] Available profiles:");
                            GetProfilesNames(false)
                                .Select(p =>
                                {
                                    if (aleloDefaultProfile == p)
                                        p += " (Current default profile)";

                                    return p;
                                })
                                .ToList()
                                .ForEach(p => WriteLine($" - {p}"));
                        }
                           
                        if (!string.IsNullOrEmpty(delete))
                        {
                            delete = delete.Trim();

                            if (!GetProfilesNames(false).Contains(delete))
                            {
                                WriteLine("[!] Profile not found!");
                                Environment.Exit(1);
                            }

                            File.Delete(Path.Combine(aleloHome, delete + ".json"));
                            WriteLine($"[+] Profile {delete} removed");
                        }

                        if (!string.IsNullOrEmpty(create))
                            await CreateProfile(create);

                        if (!string.IsNullOrEmpty(authenticate))
                            await AuthenticateProfile(authenticate);

                        // TODO:
                        // - Add the logic :v
                    });

                return profileCommand;
            }

            Command Card()
            {
                var cardCommand = new Command("card",
                    "Select default and list user cards")
                {
                    new Option<bool>(new[] {"--list", "-l"})
                    {
                        Description =
                            $"List available cards under current profile ({(string.IsNullOrEmpty(aleloDefaultProfile) ? "No profiles created" : aleloDefaultProfile)})"
                    }
                };

                cardCommand.Handler =
                    CommandHandler.Create<bool>(list =>
                    {
                        // TODO:
                        // - Add the logic :v
                    });

                return cardCommand;
            }

            Option Statement()
            {
                var statementOption = new Option(new[] {"-s", "--statement"})
                {
                    Description =
                        $"List last transactions for the default card ({(string.IsNullOrEmpty(aleloDefaultProfile) ? "No profiles created for take card" : aleloDefaultProfile)})"
                };

                return statementOption;
            }

            static Option Verbose()
            {
                var statementOption = new Option(new[] {"-v", "--verbose"})
                {
                    Description = "Increase the application verbose"
                };

                return statementOption;
            }

            static Option ApplicationEnvironment()
            {
                var statementOption = new Option(new[] { "-e", "--env" })
                {
                    Description = "Show application environment variables"
                };

                return statementOption;
            }

            #endregion

            #region Helpers

            string GetAleloGatewayToken()
            {
                // See helpers/generate_jwt.py
                // this secret key has been extracted
                // from a hardcoded key on Alelo's 
                // React application frontend.
                const string key = "<hb(yk%YK8s{tw6T";

                return new JwtSecurityTokenHandler()
                    .WriteToken(new JwtSecurityToken(new JwtHeader(new SigningCredentials
                    (new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                        SecurityAlgorithms.HmacSha256)), new JwtPayload
                    {
                        {"iss", "meualelo.alelo.com.br"},
                        {"sub", "meualelo"},
                        {"exp", new DateTimeOffset(DateTime.UtcNow.AddHours(24)).ToUnixTimeSeconds()},
                        {"fnp", "fe2ae307aceb5898dd89799241a55676"},
                        {"src", "WEB"}
                    }));
            }

            IEnumerable<string> GetProfilesNames(bool withExtension) =>
                withExtension
                    ? Directory.GetFiles(aleloHome)
                        .Where(f => f.EndsWith(".json"))
                        .Select(Path.GetFileName)
                    : Directory.GetFiles(aleloHome)
                        .Where(f => f.EndsWith(".json"))
                        .Select(f => f.Replace(".json", string.Empty))
                        .Select(Path.GetFileName);


            async Task<IEnumerable<Profile>> GetProfiles()
            {
                var collection = new List<Profile>();
                
                foreach (var profileName in GetProfilesNames(true))
                    collection.Add(
                        JsonSerializer.Deserialize<Profile>(
                            await File.ReadAllTextAsync(Path.Combine(aleloHome, profileName))));
                
                return collection;
            }

            #endregion

            var commands = new RootCommand
            {
                Profile(),
                Card(),
                Statement(),
                Verbose(),
                ApplicationEnvironment()
            };

            commands.Handler = CommandHandler.Create<bool, bool, bool>((statement, verbose, env) =>
            {
                if (!GetProfilesNames(false).Any())
                {
                    WriteLine("[!] No profiles found, create one first!");
                    WriteLine(" > Try --help");

                    Environment.Exit(1);
                }
                
                if (verbose)
                    globalVerbose = true;

                if (env)
                {
                    WriteLine("[+] Application environment variables:");

                    var envHome = Environment.GetEnvironmentVariable("ALELO_HOME");
                    var envProfile = Environment.GetEnvironmentVariable("ALELO_DEFAULT_PROFILE");
                    var envCard = Environment.GetEnvironmentVariable("ALELO_DEFAULT_CARD");

                    WriteLine(string.IsNullOrEmpty(envHome)
                        ? $" > ALELO_HOME is empty, default value in use: {aleloHome}"
                        : $" > ALELO_HOME {envHome}");

                    WriteLine(string.IsNullOrEmpty(envProfile)
                        ? $" > ALELO_DEFAULT_PROFILE is empty, default value in use: {(string.IsNullOrEmpty(aleloDefaultProfile) ? "No profiles created" : aleloDefaultProfile)}"
                        : $" > ALELO_DEFAULT_PROFILE {envProfile}");

                    WriteLine(string.IsNullOrEmpty(envCard)
                        ? $" > ALELO_DEFAULT_CARD is empty, default value in use: {(string.IsNullOrEmpty(aleloDefaultCard) ? "Card not found, check if you have a active profile session" : aleloDefaultCard)}"
                        : $" > ALELO_DEFAULT_CARD {envCard}");
                }

                if (GetProfilesNames(false).Count() > 1 && string.IsNullOrEmpty(aleloDefaultProfile))
                {
                    WriteLine("[!] More than one profile found!");
                    WriteLine(" > Try --help");

                    Environment.Exit(1);
                }

                if (globalVerbose)
                    WriteLine($"[VERBOSE] Selected profile {aleloDefaultProfile}");
            });

            commands.Description = "Meu Alelo as a command line interface, but better";
            return await commands.InvokeAsync(args).ConfigureAwait(true);
        }
    }

    internal class Profile
    {
        public string Name { get; set; }

        public Session Session { get; set; }
    }

    internal class Session
    {
        [JsonPropertyName("token")] public string Token { get; set; }
        [JsonPropertyName("email")] public string Email { get; set; }
        [JsonPropertyName("firstName")] public string FirstName { get; set; }
        [JsonPropertyName("lastName")] public string LastName { get; set; }
        [JsonIgnore] public string FullName => $"{FirstName} {LastName}";
        [JsonPropertyName("cpf")] public string Cpf { get; set; }
        [JsonPropertyName("userId")] public string UserId { get; set; }
    }
}