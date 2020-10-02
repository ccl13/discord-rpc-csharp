using DiscordRPC.Helper;
using DiscordRPC.Message;
using DiscordRPC.IO;
using DiscordRPC.RPC.Commands;
using DiscordRPC.RPC.Payload;
using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using DiscordRPC.Logging;
using DiscordRPC.Events;

namespace DiscordRPC.RPC
{
    /// <summary>
    /// Communicates between the client and discord through RPC
    /// </summary>
    internal class RpcConnection : IDisposable
    {
        /// <summary>
        /// Version of the RPC Protocol
        /// </summary>
        public static readonly int VERSION = 1;

        /// <summary>
        /// The rate of poll to the discord pipe.
        /// </summary>
        public static readonly int POLL_RATE = 1000;

        /// <summary>
        /// Should we send a null presence on the fairwells?
        /// </summary>
        private static readonly bool CLEAR_ON_SHUTDOWN = true;

        /// <summary>
        /// Should we work in a lock step manner? This option is semi-obsolete and may not work as expected.
        /// </summary>
        private static readonly bool LOCK_STEP = false;

        /// <summary>
        /// The logger used by the RPC connection
        /// </summary>
        public ILogger Logger
        {
            get { return _logger; }
            set
            {
                _logger = value;
                if (_namedPipe != null)
                {
                    _namedPipe.Logger = value;
                }
            }
        }
        private ILogger _logger;

        /// <summary>
        /// Called when a message is received from the RPC and is about to be enqueued. This is cross-thread and will execute on the RPC thread.
        /// </summary>
        public event OnRpcMessageEvent OnRpcMessage;

        #region States

        /// <summary>
        /// The current state of the RPC connection
        /// </summary>
        public RpcState State
        {
            get
            {
                var tmp = RpcState.Disconnected;
                lock (_stateLockObject)
                {
                    tmp = _state;
                }
                return tmp;
            }
        }
        private RpcState _state;
        private readonly object _stateLockObject = new object();

        /// <summary>
        /// The configuration received by the Ready
        /// </summary>
        public Configuration Configuration
        {
            get
            {
                Configuration tmp = null;
                lock (_configurationLockObject)
                {
                    tmp = _configuration;
                }
                return tmp;
            }
        }
        private Configuration _configuration = null;
        private readonly object _configurationLockObject = new object();

        // TODO: Wrong usage of volatile.
        private volatile bool _aborting = false;
        private volatile bool _shutdown = false;

        /// <summary>
        /// Indicates if the RPC connection is still running in the background
        /// </summary>
        public bool IsRunning { get { return _processMessageThread != null; } }

        /// <summary>
        /// Forces the <see cref="Close"/> to call <see cref="Shutdown"/> instead, safely saying goodbye to Discord. 
        /// <para>This option helps prevents ghosting in applications where the Process ID is a host and the game is executed within the host (ie: the Unity3D editor). This will tell Discord that we have no presence and we are closing the connection manually, instead of waiting for the process to terminate.</para>
        /// </summary>
        public bool ShutdownOnly { get; set; }

        #endregion

        #region Privates

        /// <summary>
        /// ID of the Discord APP
        /// </summary>
        private string _discordApplicationId;
        /// <summary>
        /// ID of the process to track
        /// </summary>
        private int _discordApplicationProcessId;

        /// <summary>
        /// Current command index (Nonce)
        /// </summary>
        private long _currentNonce;

        /// <summary>
        /// Read Loop
        /// </summary>
        private Thread _processMessageThread;

        /// <summary>
        /// Process loop
        /// </summary>
        private Thread _processCommandThread;

        /// <summary>
        /// Named Pipe client
        /// </summary>
        private INamedPipeClient _namedPipe;

        /// <summary>
        /// The pipe number to target. Leave as -1 for any available pipe.
        /// </summary>
        private int _currentTargetPipeNumber;

        /// <summary>
        /// Lock for the send queue
        /// </summary>
        private readonly object _rtQueueLock = new object();
        /// <summary>
        /// Max send queue size
        /// </summary>
        private readonly uint _maxRtQueueSize;
        /// <summary>
        /// The send queue
        /// </summary>
        private Queue<ICommand> _rtQueue;

        /// <summary>
        /// Lock for the receive queue
        /// </summary>
        private readonly object _rxQueueLock = new object();
        /// <summary>
        /// Max receive queue size
        /// </summary>
        private readonly uint _maxRxQueueSize;
        /// <summary>
        /// The receive queue
        /// </summary>
        private Queue<IMessage> _rxQueue;

        /// <summary>
        /// The send queue has update signal
        /// </summary>
        private AutoResetEvent _receivedMessageSignal = new AutoResetEvent(false);
        /// <summary>
        /// The process queue has update signal
        /// </summary>
        private AutoResetEvent _receivedCommandSignal = new AutoResetEvent(false);

        /// <summary>
        /// The backoff delay before reconnecting
        /// </summary>
        private BackoffDelay _delay;

        #endregion

        /// <summary>
        /// Creates a new instance of the RPC.
        /// </summary>
        /// <param name="applicationID">The ID of the Discord App</param>
        /// <param name="processID">The ID of the currently running process</param>
        /// <param name="targetPipe">The target pipe to connect too</param>
        /// <param name="client">The pipe client we shall use.</param>
        /// <param name="maxRxQueueSize">The maximum size of the out queue</param>
        /// <param name="maxRtQueueSize">The maximum size of the in queue</param>
        public RpcConnection(
            string applicationID,
            int processID,
            int targetPipe,
            INamedPipeClient client,
            uint maxRxQueueSize = 128,
            uint maxRtQueueSize = 512)
        {
            this._discordApplicationId = applicationID;
            this._discordApplicationProcessId = processID;
            this._currentTargetPipeNumber = targetPipe;
            this._namedPipe = client;
            this.ShutdownOnly = true;

            //Assign a default logger
            Logger = new ConsoleLogger();

            _delay = new BackoffDelay(500, 60 * 1000);
            _maxRtQueueSize = maxRtQueueSize;
            _rtQueue = new Queue<ICommand>((int)_maxRtQueueSize + 1);

            _maxRxQueueSize = maxRxQueueSize;
            _rxQueue = new Queue<IMessage>((int)_maxRxQueueSize + 1);

            _currentNonce = 0;
        }


        private long GetNextNonce()
        {
            _currentNonce += 1;
            return _currentNonce;
        }

        #region Queues
        /// <summary>
        /// Enqueues a command
        /// </summary>
        /// <param name="command">The command to enqueue</param>
        internal void EnqueueCommand(ICommand command)
        {
            if (command == null)
            {
                Logger.Warning("Enqueue null command?");
                return;
            }

            Logger.Trace($"Enqueue Command: {command.GetType().FullName}");

            //We cannot add anything else if we are aborting or shutting down.
            if (_aborting || _shutdown)
            {
                return;
            }

            //Enqueue the set presence argument
            lock (_rtQueueLock)
            {
                //If we are too big drop the last element
                if (_rtQueue.Count == _maxRtQueueSize)
                {
                    Logger.Error("Too many enqueued commands, dropping oldest one. Maybe you are pushing new presences too fast?");
                    _rtQueue.Dequeue();
                }

                //Enqueue the message
                _rtQueue.Enqueue(command);
                _receivedCommandSignal.Set();
            }
        }

        /// <summary>
        /// Adds a message to the message queue. Does not copy the message, so be sure to copy it yourself or dereference it.
        /// </summary>
        /// <param name="message">The message to add</param>
        private void EnqueueMessage(IMessage message)
        {
            //Invoke the message
            try
            {
                if (OnRpcMessage != null)
                {
                    OnRpcMessage.Invoke(this, message);
                }
            }
            catch (Exception e)
            {
                Logger.Error("Unhandled Exception while processing event: {0}", e.GetType().FullName);
                Logger.Error(e.Message);
                Logger.Error(e.StackTrace);
            }

            //Small queue sizes should just ignore messages
            if (_maxRxQueueSize <= 0)
            {
                Logger.Trace("Enqueued Message, but queue size is 0.");
                return;
            }

            //Large queue sizes should keep the queue in check
            Logger.Trace($"Enqueue Message: {message.Type}");
            lock (_rxQueueLock)
            {
                //If we are too big drop the last element
                if (_rxQueue.Count == _maxRxQueueSize)
                {
                    Logger.Warning("Too many enqueued messages, dropping oldest one.");
                    _rxQueue.Dequeue();
                }

                //Enqueue the message
                _rxQueue.Enqueue(message);
            }
        }

        /// <summary>
        /// Dequeues a single message from the event stack. Returns null if none are available.
        /// </summary>
        /// <returns></returns>
        internal IMessage DequeueMessage()
        {
            //Logger.Trace("Deque Message");
            lock (_rxQueueLock)
            {
                //We have nothing, so just return null.
                if (_rxQueue.Count == 0)
                {
                    return null;
                }

                //Get the value and remove it from the list at the same time
                return _rxQueue.Dequeue();
            }
        }

        /// <summary>
        /// Dequeues all messages from the event stack. 
        /// </summary>
        /// <returns></returns>
        internal IMessage[] DequeueAllMessages()
        {
            //Logger.Trace("Deque Multiple Messages");
            lock (_rxQueueLock)
            {
                //Copy the messages into an array
                IMessage[] messages = _rxQueue.ToArray();

                //Clear the entire queue
                _rxQueue.Clear();

                //return the array
                return messages;
            }
        }
        #endregion

        /// <summary>
        /// Main thread loop
        /// </summary>
        private void MainLoop()
        {
            //initialize the pipe
            Logger.Info("RPC Connection Started");
            if (Logger.Level <= LogLevel.Trace)
            {
                Logger.Trace("============================");
                Logger.Trace("Assembly:             " + System.Reflection.Assembly.GetAssembly(typeof(RichPresence)).FullName);
                Logger.Trace("Pipe:                 " + _namedPipe.GetType().FullName);
                Logger.Trace("Platform:             " + Environment.OSVersion.ToString());
                Logger.Trace("applicationID:        " + _discordApplicationId);
                Logger.Trace("targetPipe:           " + _currentTargetPipeNumber);
                Logger.Trace("POLL_RATE:            " + POLL_RATE);
                Logger.Trace("_maxRtQueueSize:      " + _maxRtQueueSize);
                Logger.Trace("_maxRxQueueSize:      " + _maxRxQueueSize);
                Logger.Trace("============================");
            }

            //Forever trying to connect unless the abort signal is sent
            //Keep Alive Loop
            while (!_aborting && !_shutdown)
            {
                try
                {
                    //Wrap everything up in a try get
                    //Dispose of the pipe if we have any (could be broken)
                    if (_namedPipe == null)
                    {
                        Logger.Error("Something bad has happened with our pipe client!");
                        _aborting = true;
                        return;
                    }

                    //Connect to a new pipe
                    Logger.Trace("Connecting to the pipe through the {0}", _namedPipe.GetType().FullName);
                    if (_namedPipe.Connect(_currentTargetPipeNumber))
                    {
                        #region Connected
                        //We connected to a pipe! Reset the delay
                        Logger.Trace("Connected to the pipe. Attempting to establish handshake...");
                        EnqueueMessage(new ConnectionEstablishedMessage() { ConnectedPipe = _namedPipe.ConnectedPipe });

                        //Attempt to establish a handshake
                        EstablishHandshake();
                        Logger.Trace("Connection Established. Starting reading loop...");

                        //Continously iterate, waiting for the frame
                        //We want to only stop reading if the inside tells us (mainloop), if we are aborting (abort) or the pipe disconnects
                        // We dont want to exit on a shutdown, as we still have information
                        bool isCloseRequired = false;
                        while (!isCloseRequired && !_aborting && !_shutdown && _namedPipe.IsConnected)
                        {
                            PipeFrame frame;
                            #region Read Loop

                            //Iterate over every frame we have queued up, processing its contents
                            if (_namedPipe.ReadFrame(out frame))
                            {
                                //Do some basic processing on the frame
                                ProcessRawFrame(frame, out isCloseRequired);
                            }
                            else if (!_aborting && _namedPipe.IsConnected)
                            {
                                //Wait for some time, or until a command has been queued up
                                _receivedMessageSignal.WaitOne(POLL_RATE);
                            }

                            #endregion
                        }
                        #endregion

                        Logger.Trace("Left main read loop for some reason. Aborting: {0}, Shutting Down: {1}", _aborting, _shutdown);
                    }
                    else
                    {
                        Logger.Error("Failed to connect for some reason.");
                        EnqueueMessage(new ConnectionFailedMessage() { FailedPipe = _currentTargetPipeNumber });
                    }

                    //If we are not aborting, we have to wait a bit before trying to connect again
                    if (!_aborting && !_shutdown)
                    {
                        //We have disconnected for some reason, either a failed pipe or a bad reading,
                        // so we are going to wait a bit before doing it again
                        long sleep = _delay.NextDelay();

                        Logger.Trace("Waiting {0}ms before attempting to connect again", sleep);
                        Thread.Sleep(_delay.NextDelay());
                    }
                }
                //catch(InvalidPipeException e)
                //{
                //	Logger.Error("Invalid Pipe Exception: {0}", e.Message);
                //}
                catch (Exception e)
                {
                    Logger.Error("Unhandled Exception: {0}", e.GetType().FullName);
                    Logger.Error(e.Message);
                    Logger.Error(e.StackTrace);
                }
                finally
                {
                    //Disconnect from the pipe because something bad has happened. An exception has been thrown or the main read loop has terminated.
                    if (_namedPipe.IsConnected)
                    {
                        //Terminate the pipe
                        Logger.Trace("Closing the named pipe.");
                        _namedPipe.Close();
                    }

                    //Update our state
                    SetConnectionState(RpcState.Disconnected);
                }
            }

            //We have disconnected, so dispose of the thread and the pipe.
            Logger.Trace("Left Main Loop");
            if (_namedPipe != null)
            {
                _namedPipe.Dispose();
            }

            Logger.Info("Thread Terminated, no longer performing RPC connection.");
        }

        /// <summary>
        /// Main thread loop
        /// </summary>
        private void ProcessCommandLoop()
        {
            //Forever trying to connect unless the abort signal is sent
            //Keep Alive Loop
            while (!_aborting && !_shutdown)
            {
                try
                {
                    if (!_aborting && _namedPipe.IsConnected)
                    {
                        //Process the entire command queue we have left
                        ProcessCommandQueue();

                        //Wait for some time, or until a command has been queued up
                        _receivedCommandSignal.WaitOne(POLL_RATE);
                    }
                }
                catch (Exception e)
                {
                    Logger.Error("Unhandled Exception: {0}", e.GetType().FullName);
                    Logger.Error(e.Message);
                    Logger.Error(e.StackTrace);
                }
            }

            //We have disconnected, so dispose of the thread and the pipe.
            Logger.Trace("Left Main Loop");
            if (_namedPipe != null)
            {
                _namedPipe.Dispose();
            }

            Logger.Info("Thread Terminated, no longer performing RPC connection.");
        }

        #region Reading

        private void ProcessRawFrame(PipeFrame frame, out bool isCloseRequired)
        {
            Logger.Trace("Read Payload: {0}", frame.Opcode);
            isCloseRequired = false;
            switch (frame.Opcode)
            {
                //We have been told by discord to close, so we will consider it an abort
                case Opcode.Close:
                    ClosePayload close = frame.GetObject<ClosePayload>();
                    Logger.Warning("We have been told to terminate by discord: ({0}) {1}", close.Code, close.Reason);
                    EnqueueMessage(new CloseMessage() { Code = close.Code, Reason = close.Reason });
                    isCloseRequired = true;
                    break;

                //We have pinged, so we will flip it and respond back with pong
                case Opcode.Ping:
                    Logger.Trace("PING");
                    frame.Opcode = Opcode.Pong;
                    _namedPipe.WriteFrame(frame);
                    break;

                //We have ponged? I have no idea if Discord actually sends ping/pongs.
                case Opcode.Pong:
                    Logger.Trace("PONG");
                    break;

                //A frame has been sent, we should deal with that
                case Opcode.Frame:
                    if (_shutdown)
                    {
                        //We are shutting down, so skip it
                        Logger.Warning("Skipping frame because we are shutting down.");
                        break;
                    }

                    if (frame.Data == null)
                    {
                        //We have invalid data, thats not good.
                        Logger.Error("We received no data from the frame so we cannot get the event payload!");
                        break;
                    }

                    //We have a frame, so we are going to process the payload and add it to the stack
                    EventPayload response = null;
                    try
                    {
                        response = frame.GetObject<EventPayload>();
                    }
                    catch (Exception e)
                    {
                        Logger.Error("Failed to parse event! " + e.Message);
                        Logger.Error("Data: " + frame.Message);
                    }

                    if (response != null)
                    {
                        ProcessFrame(response);
                    }
                    break;


                default:
                case Opcode.Handshake:
                    //We have a invalid opcode, better terminate to be safe
                    Logger.Error("Invalid opcode: {0}", frame.Opcode);
                    isCloseRequired = true;
                    break;
            }
        }

        /// <summary>Handles the response from the pipe and calls appropriate events and changes states.</summary>
        /// <param name="response">The response received by the server.</param>
        private void ProcessFrame(EventPayload response)
        {
            Logger.Info("Handling Response. Cmd: {0}, Event: {1}", response.Command, response.Event);

            //Check if it is an error
            if (response.Event.HasValue && response.Event.Value == ServerEvent.Error)
            {
                //We have an error
                Logger.Error("Error received from the RPC");

                //Create the event objetc and push it to the queue
                ErrorMessage err = response.GetObject<ErrorMessage>();
                Logger.Error("Server responded with an error message: ({0}) {1}", err.Code.ToString(), err.Message);

                //Enqueue the messsage and then end
                EnqueueMessage(err);
                return;
            }

            //Check if its a handshake
            if (State == RpcState.Connecting)
            {
                if (response.Command == Command.Dispatch && response.Event.HasValue && response.Event.Value == ServerEvent.Ready)
                {
                    Logger.Info("Connection established with the RPC");
                    SetConnectionState(RpcState.Connected);
                    _delay.Reset();

                    //Prepare the object
                    ReadyMessage ready = response.GetObject<ReadyMessage>();
                    lock (_configurationLockObject)
                    {
                        _configuration = ready.Configuration;
                        ready.User.SetConfiguration(_configuration);
                    }

                    //Enqueue the message
                    EnqueueMessage(ready);
                    return;
                }
            }

            if (State == RpcState.Connected)
            {
                switch (response.Command)
                {
                    //We were sent a dispatch, better process it
                    case Command.Dispatch:
                        {
                            ProcessDispatch(response);
                        }
                        break;

                    //We were sent a Activity Update, better enqueue it
                    case Command.SetActivity:
                        {
                            if (response.Data == null)
                            {
                                EnqueueMessage(new PresenceMessage());
                            }
                            else
                            {
                                RichPresenceResponse rp = response.GetObject<RichPresenceResponse>();
                                EnqueueMessage(new PresenceMessage(rp));
                            }
                        }
                        break;

                    case Command.Unsubscribe:
                    case Command.Subscribe:
                        {
                            //Prepare a serializer that can account for snake_case enums.
                            JsonSerializer serializer = new JsonSerializer();
                            serializer.Converters.Add(new Converters.EnumSnakeCaseConverter());

                            //Go through the data, looking for the evt property, casting it to a server event
                            var evt = response.GetObject<EventPayload>().Event.Value;

                            //Enqueue the appropriate message.
                            if (response.Command == Command.Subscribe)
                                EnqueueMessage(new SubscribeMessage(evt));
                            else
                                EnqueueMessage(new UnsubscribeMessage(evt));
                        }
                        break;

                    case Command.SendActivityJoinInvite:
                        Logger.Trace("Got invite response ack.");
                        break;

                    case Command.CloseActivityJoinRequest:
                        Logger.Trace("Got invite response reject ack.");
                        break;

                    case Command.GetVoiceSettings:
                        Logger.Trace("Got get voice settings.");
                        {
                            var isMuted = response.Data.Value<bool>("mute");
                            var isDeafen = response.Data.Value<bool>("deaf");
                            var msg = new VoiceSettingsMessage()
                            {
                                IsMuted = isMuted,
                                IsDeafen = isDeafen,
                            };
                            EnqueueMessage(msg);
                        }
                        break;

                    case Command.Authorize:
                        Logger.Trace("Got Authorize response.");
                        {
                            var accessCode = response.Data.Value<string>("code");
                            EnqueueMessage(new AuthorizedMessage() { AccessCode = accessCode });
                        }
                        break;

                    case Command.Authenticate:
                        {
                            var accessToken = response.Data.Value<string>("access_token");
                            EnqueueMessage(new AuthenticatedMessage() { AccessToken = accessToken });
                        }
                        break;

                    //we have no idea what we were sent
                    default:
                        Logger.Error("Unkown frame was received! {0}", response.Command);
                        return;
                }
                return;
            }

            Logger.Trace("Received a frame while we are disconnected. Ignoring. Cmd: {0}, Event: {1}", response.Command, response.Event);
        }

        private void ProcessDispatch(EventPayload response)
        {
            if (response.Command != Command.Dispatch) return;
            if (!response.Event.HasValue) return;

            switch (response.Event.Value)
            {
                //We are to join the server
                case ServerEvent.ActivitySpectate:
                    var spectate = response.GetObject<SpectateMessage>();
                    EnqueueMessage(spectate);
                    break;

                case ServerEvent.ActivityJoin:
                    var join = response.GetObject<JoinMessage>();
                    EnqueueMessage(join);
                    break;

                case ServerEvent.ActivityJoinRequest:
                    var request = response.GetObject<JoinRequestMessage>();
                    EnqueueMessage(request);
                    break;

                case ServerEvent.VoiceSettingsUpdate:
                    var voiceSettingsMessage = response.GetObject<VoiceSettingsMessage>();
                    EnqueueMessage(voiceSettingsMessage);
                    break;

                //Unkown dispatch event received. We should just ignore it.
                default:
                    Logger.Warning("Ignoring {0}", response.Event.Value);
                    break;
            }
        }

        #endregion

        #region Writting

        internal ICommand PeekCommand()
        {
            lock (_rtQueueLock)
            {
                //We have nothing, so just return null.
                if (_rtQueue.Count == 0)
                {
                    return null;
                }

                //Get the value and remove it from the list at the same time
                return _rtQueue.Peek();
            }
        }

        internal bool DropCommand()
        {
            lock (_rtQueueLock)
            {
                if (_rtQueue.Count == 0)
                {
                    return false;
                }

                _rtQueue.Dequeue();
                return true;
            }
        }


        private void ProcessCommandQueue()
        {
            //Logger.Info("Checking command queue");

            //We are not ready yet, dont even try
            if (State != RpcState.Connected)
            {
                return;
            }

            //We are aborting, so we will just log a warning so we know this is probably only going to send the CLOSE
            if (_aborting)
            {
                Logger.Warning("We have been told to write a queue but we have also been aborted.");
            }

            //Prepare some variabels we will clone into with locks
            bool needsWriting = true;

            //Continue looping until we dont need anymore messages
            while (needsWriting && _namedPipe.IsConnected)
            {
                var currentCommand = PeekCommand();
                needsWriting = currentCommand != null;
                if (!needsWriting)
                {
                    break;
                }

                //Break out of the loop as soon as we send this item
                if (_shutdown || (!_aborting && LOCK_STEP))
                {
                    needsWriting = false;
                }

                //Prepare the payload
                IPayload payload = currentCommand.PreparePayload(GetNextNonce());
                Logger.Trace($"Attempting to send payload: {payload.Command}");

                //Prepare the frame
                PipeFrame frame = new PipeFrame();
                if (currentCommand is CloseCommand)
                {
                    //We have been sent a close frame. We better just send a handwave
                    //Send it off to the server
                    SendHandwave();

                    //Queue the item
                    Logger.Trace("Handwave sent, ending queue processing.");
                    DropCommand();

                    //Stop sending any more messages
                    return;
                }
                else
                {
                    if (_aborting)
                    {
                        //We are aborting, so just dequeue the message and dont bother sending it
                        Logger.Warning("- skipping frame because of abort.");
                        DropCommand();
                    }
                    else
                    {
                        //Prepare the frame
                        frame.SetObject(Opcode.Frame, payload);

                        //Write it and if it wrote perfectly fine, we will dequeue it
                        Logger.Trace("Sending payload: " + payload.Command);
                        if (_namedPipe.WriteFrame(frame))
                        {
                            //We sent it, so now dequeue it
                            Logger.Trace("Sent Successfully.");
                            DropCommand();
                        }
                        else
                        {
                            //Something went wrong, so just giveup and wait for the next time around.
                            Logger.Warning("Something went wrong during writing!");
                            return;
                        }
                    }
                }
            }
        }

        #endregion

        #region Connection

        /// <summary>
        /// Establishes the handshake with the server. 
        /// </summary>
        /// <returns></returns>
        private void EstablishHandshake()
        {
            Logger.Trace("Attempting to establish a handshake...");

            //We are establishing a lock and not releasing it until we sent the handshake message.
            // We need to set the key, and it would not be nice if someone did things between us setting the key.

            //Check its state
            if (State != RpcState.Disconnected)
            {
                Logger.Error("State must be disconnected in order to start a handshake!");
                return;
            }

            //Send it off to the server
            Logger.Trace("Sending Handshake...");
            if (!_namedPipe.WriteFrame(new PipeFrame(Opcode.Handshake, new Handshake() { Version = VERSION, ClientID = _discordApplicationId })))
            {
                Logger.Error("Failed to write a handshake.");
                return;
            }

            //This has to be done outside the lock
            SetConnectionState(RpcState.Connecting);
        }

        /// <summary>
        /// Establishes a fairwell with the server by sending a handwave.
        /// </summary>
        private void SendHandwave()
        {
            Logger.Info("Attempting to wave goodbye...");

            //Check its state
            if (State == RpcState.Disconnected)
            {
                Logger.Error("State must NOT be disconnected in order to send a handwave!");
                return;
            }

            //Send the handwave
            if (!_namedPipe.WriteFrame(new PipeFrame(Opcode.Close, new Handshake() { Version = VERSION, ClientID = _discordApplicationId })))
            {
                Logger.Error("failed to write a handwave.");
                return;
            }
        }


        /// <summary>
        /// Attempts to connect to the pipe. Returns true on success
        /// </summary>
        /// <returns></returns>
        public bool AttemptConnection()
        {
            Logger.Info("Attempting a new connection");

            //The thread mustn't exist already
            if (_processMessageThread != null)
            {
                Logger.Error("Cannot attempt a new connection as the previous connection thread is not null!");
                return false;
            }

            //We have to be in the disconnected state
            if (State != RpcState.Disconnected)
            {
                Logger.Warning("Cannot attempt a new connection as the previous connection hasn't changed state yet.");
                return false;
            }

            if (_aborting)
            {
                Logger.Error("Cannot attempt a new connection while aborting!");
                return false;
            }

            //Start the thread up
            _processMessageThread = new Thread(MainLoop);
            _processMessageThread.Name = "Discord IPC Thread";
            _processMessageThread.IsBackground = true;
            _processMessageThread.Start();

            _processCommandThread = new Thread(ProcessCommandLoop);
            _processCommandThread.Name = "Process Thread";
            _processCommandThread.IsBackground = true;
            _processCommandThread.Start();

            return true;
        }

        /// <summary>
        /// Sets the current state of the pipe, locking the l_states object for thread saftey.
        /// </summary>
        /// <param name="state">The state to set it too.</param>
        private void SetConnectionState(RpcState state)
        {
            Logger.Trace("Setting the connection state to {0}", state.ToString().ToSnakeCase().ToUpperInvariant());
            lock (_stateLockObject)
            {
                _state = state;
            }
        }

        /// <summary>
        /// Closes the connection and disposes of resources. This will not force termination, but instead allow Discord disconnect us after we say goodbye. 
        /// <para>This option helps prevents ghosting in applications where the Process ID is a host and the game is executed within the host (ie: the Unity3D editor). This will tell Discord that we have no presence and we are closing the connection manually, instead of waiting for the process to terminate.</para>
        /// </summary>
        public void Shutdown()
        {
            //Enable the flag
            Logger.Trace("Initiated shutdown procedure");
            _shutdown = true;

            //Clear the commands and enqueue the close
            lock (_rtQueueLock)
            {
                _rtQueue.Clear();
                if (CLEAR_ON_SHUTDOWN)
                {
                    _rtQueue.Enqueue(new PresenceCommand() { PID = _discordApplicationProcessId, Presence = null });
                }
                _rtQueue.Enqueue(new CloseCommand());
                //Trigger the event
                _receivedMessageSignal.Set();
                _receivedCommandSignal.Set();
            }
        }

        /// <summary>
        /// Closes the connection and disposes of resources.
        /// </summary>
        public void Close()
        {
            if (_processMessageThread == null)
            {
                Logger.Error("Cannot close as it is not available!");
                return;
            }

            if (_processCommandThread == null)
            {
                Logger.Error("Cannot close as it is not available!");
                return;
            }

            if (_aborting)
            {
                Logger.Error("Cannot abort as it has already been aborted");
                return;
            }

            //Set the abort state
            if (ShutdownOnly)
            {
                Shutdown();
                return;
            }

            //Terminate
            Logger.Trace("Updating Abort State...");
            _aborting = true;
            _receivedMessageSignal.Set();
            _receivedCommandSignal.Set();
        }


        /// <summary>
        /// Closes the connection and disposes resources. Identical to <see cref="Close"/> but ignores the "ShutdownOnly" value.
        /// </summary>
        public void Dispose()
        {
            ShutdownOnly = false;
            Close();
        }
        #endregion

    }

    /// <summary>
    /// State of the RPC connection
    /// </summary>
    internal enum RpcState
    {
        /// <summary>
        /// Disconnected from the discord client
        /// </summary>
        Disconnected,

        /// <summary>
        /// Connecting to the discord client. The handshake has been sent and we are awaiting the ready event
        /// </summary>
        Connecting,

        /// <summary>
        /// We are connect to the client and can send and receive messages.
        /// </summary>
        Connected
    }
}