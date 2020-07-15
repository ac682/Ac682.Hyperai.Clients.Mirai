using Ac682.Hyperai.Clients.Mirai.Serialization;
using Hyperai.Events;
using Hyperai.Messages;
using Hyperai.Messages.ConcreteModels;
using Hyperai.Relations;
using Hyperai.Services;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Ac682.Hyperai.Clients.Mirai
{
    public class MiraiHttpSession : IDisposable
    {
        public ApiClientConnectionState State { get; private set; } = ApiClientConnectionState.Disconnected;
        private readonly string _host;
        private readonly int _port;
        private readonly string _authKey;
        private readonly long _selfQQ;


        private readonly ApiHttpClient _client;
        private readonly JsonFormatter _formatter = new JsonFormatter();
        private readonly JsonParser _parser = new JsonParser();
        private string sessionKey = null;

        public MiraiHttpSession(string host, int port, string authKey, long selfQQ)
        {
            _host = host;
            _port = port;
            _authKey = authKey;
            _selfQQ = selfQQ;

            _client = new ApiHttpClient($"http://{host}:{port}");
        }

        public GenericEventArgs PullEvent()
        {
            var fetch = _client.GetAsync($"fetchLatestMessage?sessionKey={sessionKey}&count=1").GetAwaiter().GetResult().GetJsonObjectAsync().GetAwaiter().GetResult();
            foreach (var evt in fetch.Value<JArray>("data"))
            {
                switch (evt.Value<string>("type"))
                {
                    case "FriendMessage":
                        return new FriendMessageEventArgs()
                        {
                            Message = _parser.Parse(evt.Value<JArray>("messageChain").ToString()),
                            User = new Friend()
                            {
                                Identity = evt["sender"].Value<long>("id"),
                                Nickname = evt["sender"].Value<string>("nickname"),
                                Remark = evt["sender"].Value<string>("remark")
                            }
                        };
                    case "GroupMessage":
                        var args = new GroupMessageEventArgs()
                        {
                            Message = _parser.Parse(evt.Value<JArray>("messageChain").ToString()),
                            Group = new Group()
                            {
                                Identity = evt["sender"]["group"].Value<long>("id"),
                                Name = evt["sender"]["group"].Value<string>("name"),
                            },
                            User = new Member()
                            {
                                Identity = evt["sender"].Value<long>("id"),
                                DisplayName = evt["sender"].Value<string>("memberName"),
                                Role = evt["sender"].Value<string>("permission") switch { "OWNER" => GroupRole.Owner, "ADMINISTRATOR" => GroupRole.Administrator, _ => GroupRole.Member }
                            }
                        };
                        args.User.Group = args.Group;
                        return args;
                    default:
                        break;
                }
            }
            return null;
        }

        public void Connect()
        {

            var auth = _client.PostObjectAsync("auth", new { authKey = _authKey }).GetAwaiter().GetResult().GetJsonObjectAsync().GetAwaiter().GetResult();
            sessionKey = auth.Value<string>("session");
            if (auth.Value<int>("code") == -1)
            {
                throw new ArgumentException("Wrong MIRAI API HTTP auth key");
            }

            var verify = _client.PostObjectAsync("verify", new { sessionKey = this.sessionKey, qq = _selfQQ }).GetAwaiter().GetResult().GetJsonObjectAsync().GetAwaiter().GetResult();
            if (verify.Value<int>("code") != 0) throw new ArgumentException(verify.Value<string>("msg"));

            State = ApiClientConnectionState.Connected;
        }

        public async Task<IEnumerable<Friend>> GetFriendsAsync()
        {
            var friends = await (await _client.GetAsync($"friendList?sessionKey={sessionKey}")).GetJsonObjectAsync();
            var list = new List<Friend>();
            foreach (var friend in friends)
            {
                list.Add(new Friend()
                {
                    Identity = friend.Value<long>("id"),
                    Nickname = friend.Value<string>("nickname"),
                    Remark = friend.Value<string>("remark")
                });
            }
            return list;
        }

        public async Task<IEnumerable<Group>> GetGroupsAsync()
        {
            var groups = await (await _client.GetAsync($"groupList?sessionKey={sessionKey}")).GetJsonObjectAsync();
            var list = new List<Group>();
            foreach (var group in groups)
            {
                var ins = new Group()
                {
                    Identity = group.Value<long>("id"),
                    Name = group.Value<string>("name")
                };
                // TODO: uncomment when it wont cause error
                // var members = await GetMembersAsync(ins);
                // ins.Members = members;
                list.Add(ins);
            }
            return list;
        }

        private async Task<IEnumerable<Member>> GetMembersAsync(Group group)
        {
            var members = await (await _client.GetAsync($"memberList?sessionKey={sessionKey}")).GetJsonObjectAsync();
            var list = new List<Member>();
            foreach (var member in members)
            {
                list.Add(new Member()
                {
                    Identity = member.Value<long>("id"),
                    DisplayName = member.Value<string>("memberName"),
                    Role = member.Value<string>("permission") switch { "OWNER" => GroupRole.Owner, "ADMINISTRATOR" => GroupRole.Administrator, _ => GroupRole.Member },
                    Group = group
                });
            }
            return list;
        }

        public async Task<long> SendFriendMessageAsync(Friend friend, MessageChain chain)
        {
            await PreprocessChainAsync(chain, MessageEventType.Friend);
            var response = await _client.PostObjectAsync("sendFriendMessage", new { sessionKey = this.sessionKey, target = friend.Identity, messageChain = chain.AsReadable() });
            var message = await response.GetJsonObjectAsync();
            if (message.Value<int>("code") != 0)
            {
                throw new Exception(message.Value<string>("msg"));
            }
            return message.Value<long>("messageId");
        }

        public async Task<long> SendGroupMessageAsync(Group group, MessageChain chain)
        {
            await PreprocessChainAsync(chain, MessageEventType.Group);
            var response = await _client.PostObjectAsync("sendGroupMessage", new { sessionKey = this.sessionKey, target = group.Identity, messageChain = chain.AsReadable() });
            var message = await response.GetJsonObjectAsync();
            if (message.Value<int>("code") != 0)
            {
                throw new Exception(message.Value<string>("msg"));
            }
            return message.Value<long>("messageId");
        }

        private async Task PreprocessChainAsync(MessageChain chain, MessageEventType type)
        {
            foreach (var cmp in chain)
            {
                if (cmp is Image image && string.IsNullOrEmpty(image.ImageId))
                {
                    await UploadImageAsync(image, type);
                }
            }
        }

        async Task UploadImageAsync(Image image, MessageEventType type)
        {
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(sessionKey), "sessionKey");
            content.Add(new StringContent(type switch { MessageEventType.Friend => "friend", MessageEventType.Group => "group", _ => throw new NotImplementedException() }), "type");
            var imageStream = image.OpenRead();
            string format;
            using (System.Drawing.Image img = System.Drawing.Image.FromStream(imageStream))
            {
                format = img.RawFormat.ToString();
                switch (format)
                {
                    case nameof(ImageFormat.Jpeg):
                    case nameof(ImageFormat.Png):
                    case nameof(ImageFormat.Gif):
                        {
                            format = format.ToLower();
                            break;
                        }
                    default:
                        {
                            MemoryStream ms = new MemoryStream();
                            img.Save(ms, ImageFormat.Png);
                            imageStream.Dispose();
                            imageStream = ms;
                            format = "png";
                            break;
                        }
                }
            }
            imageStream.Seek(0, SeekOrigin.Begin);
            HttpContent imageContent = new StreamContent(imageStream);
            imageContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "img",
                FileName = $"{Guid.NewGuid():n}.{format}"
            };
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/" + format);
            content.Add(imageContent, "img");
            var resp = await (await _client.PostAsync("uploadImage", content)).GetJsonObjectAsync();
            image.ImageId = resp.Value<string>("imageId");
            image.Url = new Uri(resp.Value<string>("url"));
        }

        public void Disconnect()
        {
            if (State == ApiClientConnectionState.Connected)
            {
                var release = _client.PostObjectAsync("release", new { sessionKey = this.sessionKey, qq = _selfQQ }).GetAwaiter().GetResult().GetJsonObjectAsync().GetAwaiter().GetResult();
                State = ApiClientConnectionState.Disconnected;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool isDisposed = false;
        protected virtual void Dispose(bool isDisposing)
        {
            if (isDisposing && !isDisposed)
            {
                // do something
                if (State == ApiClientConnectionState.Connected)
                {
                    Disconnect();
                }
            }
        }
    }
}