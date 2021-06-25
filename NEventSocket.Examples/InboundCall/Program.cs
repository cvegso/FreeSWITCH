using System;
using System.Threading.Tasks;
using System.Reactive.Linq;
using NEventSocket;
using NEventSocket.Channels;
using NEventSocket.FreeSwitch;

/// <summary>
/// It simulates an inbound call scenario by
/// 
///    1) listening for incoming calls from customers
///    2) answering the call when a customer dials in
///    3) playing a welcome message to the customer
///    4) creating a conference
///    5) transferring the customer to the conference
///    6) playing queue music for the customer via the conference
///    7) dialing the agent in the background
///    8) transferring the agent to the conference after the agent answers the call
///    9) letting the agent and the customer to talk to each other
///    10) recording the agent - customer conversation via the conference
/// 
/// </summary>
namespace InboundCall
{
    class Program
    {
        /// <summary>
        /// The IPv4 address of the FusionPBX/FreeSWITCH instance.
        /// </summary>
        private const string freeSwitchIPv4 = "10.168.3.225";

        /// <summary>
        /// The Event Socket Port on the FusionPBX/FreeSWITCH instance.
        /// </summary>
        private const int freeSwitchEventPort = 8021;

        /// <summary>
        /// The Event Socket Password on the FusionPBX/FreeSWITCH instance.
        /// </summary>
        private const string freeSwitchEventSocketPassword = "ClueCon";

        /// <summary>
        /// The local port on the application server to listen on for incoming Event Socket 
        /// connections from FusionPBX/FreeSWITCH.
        /// </summary>
        private const int localListeningPort = 8084;

        /// <summary>
        /// The application endpoint to be dialed as customer.
        /// 
        /// NOTE: this endpoint should be provisioned in the dialplan (Dialplan Manager) as follows:
        /// 
        ///    condition       destination_number         6789
        ///    action          socket                     [application server IPv4]:8084 async full
        ///    
        /// </summary>
        private const string dialInUri = "6789";

        /// <summary>
        /// A valid SIP extension; being dialed as the agent.
        /// </summary>
        private static readonly string agentUri = $"sofia/external/4449@{freeSwitchIPv4}";

        /// <summary>
        /// The built-in FusionPBX/FreeSWITCH sound file used as welcome message.
        /// </summary>
        private const string welcomeMessageSoundFile = "ivr/8000/ivr-welcome_to_freeswitch.wav";

        /// <summary>
        /// The directory to store recordings.
        /// </summary>
        private const string recordingLocation = "/var/tmp";

        /// <summary>
        /// Listener for incoming calls from FusionPBX/FreeSWITCH.
        /// </summary>
        private static OutboundListener callReceiver = null;

        /// <summary>
        /// Client to initiate outbound calls via FusionPBX/FreeSWITCH.
        /// </summary>
        private static InboundSocket callInitiator = null;

        private static string customerChannelUuid = null;
        private static string agentChannelUuid = null;

        static void Main(string[] args)
        {
            string logLabel = nameof(Main);

            NEventSocket.Logging.Logger.Configure(new Microsoft.Extensions.Logging.LoggerFactory());

            SetupNEventSocketClient().Wait();

            /*
             * It is an inbound scenario, let's wait for calls from customers.
             */

            Console.WriteLine($"{logLabel} - Dial {dialInUri} as customer");
            Console.WriteLine($"{logLabel} - You can press 'q' to quit at any time...");

            while (Console.ReadKey().KeyChar != 'q') ;

            /*
             * Cleaning up resources.
             */

            TerminateCalls().Wait();
            DestroyNEventSocketClient();
        }

        /// <summary>
        /// It sets up NEventSocket related inbound/outbound resources and starts listening for 
        /// incoming calls.
        /// </summary>
        /// <returns></returns>
        private static async Task SetupNEventSocketClient()
        {
            string logLabel = nameof(SetupNEventSocketClient);

            /*
             * Let's setup InboundSocket used to initiated outbound calls.
             */

            callInitiator = await InboundSocket.Connect(freeSwitchIPv4, freeSwitchEventPort, freeSwitchEventSocketPassword);

            callInitiator.ChannelEvents.Subscribe(e =>
            {
                Console.WriteLine($"{logLabel} -  ChannelEvent is received. ChannelCallUUID: {e.ChannelCallUUID}, " +
                    $"EventName: {e.EventName}, ChannelState: {e.ChannelState}, AnswerState: {e.AnswerState}");

                if (e.AnswerState == AnswerState.Answered)
                {
                    /*
                     * These AnswerState events seem to be totally unreliable.
                     */

                    if (agentChannelUuid != null && e.ChannelCallUUID == agentChannelUuid)
                    {
                        Console.WriteLine($"{logLabel} - Agent answered the call");
                    }
                }
            });

            /*
             * Let's setup OutboundListener used to receive inbound calls.
             */

            callReceiver = new OutboundListener(localListeningPort);

            callReceiver.Channels.Subscribe(
                async channel =>
                {
                    await RunInboundScenario(channel);
                });

            callReceiver.Start();
        }

        /// <summary>
        /// It starts executing the inbound call scenario for the specified customer call.
        /// </summary>
        /// <param name="customerChannel"></param>
        /// <returns></returns>
        private static async Task RunInboundScenario(Channel customerChannel)
        {
            /*
             * Let's answer the customer's call.
             */

            if (await AnswerCall(customerChannel))
            {
                customerChannelUuid = customerChannel.Uuid;

                /*
                 * Let's play welcome message to the customer.
                 */

                await PlayP2PMessage(customerChannel, welcomeMessageSoundFile);

                /*
                 * Let's escalate customer's call to conference.
                 */

                string conferenceId = await EscalateCallToConference(customerChannel);

                /*
                 * Let's dial the agent in the background.
                 */

                agentChannelUuid = await Dial(agentUri);

                /*
                 * Let's join the agent to the conference.
                 */

                await JoinCallToConference(agentChannelUuid, conferenceId);

                /*
                 * Let's start recording.
                 */

                await StartRecordingOnChannel(customerChannel);
            }
        }

        /// <summary>
        /// It releases NEventSocket related inbound/outbound resources.
        /// </summary>
        private static void DestroyNEventSocketClient()
        {
            callInitiator.Dispose();
            callInitiator = null;

            callReceiver.Stop();
            callReceiver.Dispose();
            callReceiver = null;
        }

        /// <summary>
        /// It answers the specified incoming calls.
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        private static async Task<bool> AnswerCall(Channel channel)
        {
            string logLabel = nameof(AnswerCall);

            try
            {
                Console.WriteLine($"{logLabel} -  Answering call with Uuid: {channel.Uuid}. ChannelState: {channel.ChannelState}");

                /*
                 * Let's asnwer the customer call.
                 */

                await channel.Answer();

                Console.WriteLine($"{logLabel} -  Call is answered with Uuid: {channel.Uuid}. ChannelState: {channel.ChannelState}");

                return true;
            }
            catch(Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to handle the call. Reason: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// It starts playing the specified message to the remote party of the specified call.
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="media"></param>
        /// <returns></returns>
        private static async Task<bool> PlayP2PMessage(Channel channel, string media)
        {
            string logLabel = nameof(PlayP2PMessage);

            try
            {
                Console.WriteLine($"{logLabel} - Playing media: {media} to channel: {channel.Uuid}");

                /*
                 * NOTE: awaiting on the Task returned by Play() will block until playing 
                 * the messages finishes. Let's await.
                 * 
                 * NOTE: it seems there is no NEventSocket functionality to monitor the progress 
                 * of a message being played. So the only way here to detect when playing the  
                 * message finished is to await on the Task.
                 */

                PlayResult playResult = await channel.Play(media);

                return playResult.Success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to play message. Reason: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// It escalates the specified call to conference.
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        private static async Task<string> EscalateCallToConference(Channel channel)
        {
            string logLabel = nameof(EscalateCallToConference);

            try
            {
                string conferenceId = $"AdHocConference_{channel.Uuid}";

                /*
                 * NOTE: awaiting on the Task returned by Execute("conference") will block until  
                 * the conference finishes. Let's not await.
                 */

                Task task = channel.Execute("conference", conferenceId);

                return await Task.FromResult(conferenceId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to escalate call to conference. Reason: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// It dials the specified party/uri.
        /// </summary>
        /// <param name="uri"></param>
        /// <returns></returns>
        private static async Task<string> Dial(string uri)
        {
            string logLabel = nameof(Dial);

            try
            {
                Console.WriteLine($"{logLabel} - Dialing uri: {uri}");

                string channelUuid = Guid.NewGuid().ToString();

                OriginateOptions originateOptions = new OriginateOptions()
                {
                    IgnoreEarlyMedia = false,
                    TimeoutSeconds = 30,
                    UUID = channelUuid
                };

                /*
                 * NOTE: awaiting on the Task returned by Originate() will block only until  
                 * the dialing is initiated. Let's await.
                 * 
                 * NOTE: it seems there is no NEventSocket functionality to monitor the progress 
                 * of an ongoing outbound call. So we do not know when the agent answers the call
                 * here.
                 */

                OriginateResult originateResult = await callInitiator.Originate(uri, options: originateOptions);

                Console.WriteLine($"{logLabel} - Uri: {uri} is dialed. Success: {originateResult.Success}");

                return originateResult.Success ? channelUuid : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to dial the uri. Reason: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// It joins the call having the specified channel Uuid to the 
        /// </summary>
        /// <param name="channelUuid"></param>
        /// <param name="conferenceId"></param>
        /// <returns></returns>
        private static async Task<bool> JoinCallToConference(string channelUuid, string conferenceId)
        {
            string logLabel = nameof(JoinCallToConference);

            try
            {
                Console.WriteLine($"{logLabel} - Joining call with Uuid: {channelUuid} to conference: {conferenceId}");

                /*
                 * NOTE: awaiting on the Task returned by ExecuteApplication("conference") will block until  
                 * the conference finishes. Let's not await.
                 */

                Task task = callInitiator.ExecuteApplication(channelUuid, "conference", conferenceId);

                Console.WriteLine($"{logLabel} - Call with Uuid: {channelUuid} is joined to conference: {conferenceId}");

                return await Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to join call to conference. Reason: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// It terminates the customer/agent calls and the conference.
        /// </summary>
        private static async Task TerminateCalls()
        {
            string logLabel = nameof(TerminateCalls);

            Console.WriteLine($"{logLabel} - Terminating calls and the conference");

            await TerminateChannel(customerChannelUuid);
            await TerminateChannel(agentChannelUuid);

            customerChannelUuid = null;
            agentChannelUuid = null;
        }

        /// <summary>
        /// It terminates the specified channel/call.
        /// </summary>
        /// <param name="channelUuid"></param>
        private static async Task<bool> TerminateChannel(string channelUuid)
        {
            string logLabel = nameof(TerminateChannel);

            try
            {
                if (channelUuid != null)
                {
                    Console.WriteLine($"{logLabel} - Terminating channel: {channelUuid}");

                    await callInitiator.ExecuteApplication(channelUuid, "hangup");

                    return await Task.FromResult(true);
                }
                else
                {
                    Console.WriteLine($"{logLabel} - No need to terminate the channel");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to terminate channel. Reason: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// It starts call recording via the specified channel.
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        private static async Task<string> StartRecordingOnChannel(Channel channel)
        {
            string logLabel = nameof(StartRecordingOnChannel);

            try
            {
                string recordingId = Guid.NewGuid().ToString("N");
                string recordingFilePath = $"{recordingLocation}/{recordingId}.wav";

                Console.WriteLine($"{logLabel} - Starting call recording on the channel: {channel.Uuid}");

                await channel.StartRecording(recordingFilePath);

                Console.WriteLine($"{logLabel} - Recording is started with name: {recordingId}, file path: {recordingFilePath}");

                return recordingId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{logLabel} - Failed to start recording. Reason: {ex.Message}");
                return null;
            }
        }
    }
}