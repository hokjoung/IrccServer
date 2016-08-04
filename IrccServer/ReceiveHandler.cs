﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Ircc;
using static Ircc.IrccHelper;
using StackExchange.Redis;

namespace IrccServer
{
    class ReceiveHandler
    {
        ClientHandle client;
        ServerHandle server;
        Packet recvPacket;
        RedisHelper redis;
        static Dictionary<long, ClientHandle> lobbyClients;
        static List<ServerHandle> peerServers;
        static Dictionary<long, Room> rooms;
        static Dictionary<long, int[]> peerRespWait; //key: userId, value [0]: seqc, [1]: current seq count, [2]: success
        Header NoResponseHeader = new Header(-1, 0, 0);

        public ReceiveHandler()
        {
            lobbyClients = new Dictionary<long, ClientHandle>();
            peerServers = new List<ServerHandle>();
            rooms = new Dictionary<long, Room>();
            peerRespWait = new Dictionary<long, int[]>();
        }

        public ReceiveHandler(ClientHandle client, Packet recvPacket, RedisHelper redis)
        {
            this.client = client;
            this.recvPacket = recvPacket;
            this.redis = redis;
        }

        public ReceiveHandler(ServerHandle server, Packet recvPacket)
        {
            this.server = server;
            this.recvPacket = recvPacket;
        }

        public void SetPeerServers(string[] peerInfo)
        {
            foreach (string peerAddress in peerInfo)
            {
                Socket so = ConnectToPeer(peerAddress);
                if (null != so)
                {
                    ServerHandle peer = new ServerHandle(so);
                    AddPeerServer(peer);
                }
            }
        }

        public void AddPeerServer(ServerHandle peer)
        {
            if(!peerServers.Contains(peer))
                peerServers.Add(peer);
        }

        public static void RemoveClient(ClientHandle client)
        {
            if (client.Status == ClientHandle.State.Room)
            {
                Room requestedRoom;
                lock (rooms)
                {
                    if (!rooms.TryGetValue(client.RoomId, out requestedRoom))
                        Console.WriteLine("ERROR: REMOVECLIENT - room doesn't exist {0}", client.RoomId);
                    else
                        requestedRoom.RemoveClient(client);
                }
            }
            else if (client.Status == ClientHandle.State.Lobby)
            {
                lock (lobbyClients)
                    lobbyClients.Remove(client.UserId);
            }
            else
                Console.WriteLine("ERROR: REMOVECLIENT - you messed up");
        }
        public Packet ResponseCreate(Packet recvPacket, RedisHelper redis)
        {
            Packet response;
            Header returnHeader;
            byte[] returnData;
            // bytes to roomname
            int dataSize = recvPacket.header.size;
            byte[] roomnameBytes = new byte[dataSize];
            Array.Copy(recvPacket.data, 0, roomnameBytes, 0, dataSize);

            string roomname = Encoding.UTF8.GetString(roomnameBytes).Trim();
            long roomId = redis.CreateRoom(roomname);

            if (-1 == roomId)
            {
                //make packet for room duplicate
                returnHeader = new Header(Comm.CS, Code.CREATE_DUPLICATE_ERR, 0);
                returnData = null;
            }
            else
            {
                //add room to dictionary
                Room requestedRoom = new Room(roomId, roomname);
                lock (rooms)
                    rooms.Add(roomId, requestedRoom);

                //make packet for room create success
                byte[] roomIdBytes = BitConverter.GetBytes(roomId);
                returnHeader = new Header(Comm.CS, Code.CREATE_RES, roomIdBytes.Length);
                returnData = roomIdBytes;
                /*
                createAndJoin = true;
                goto case Code.JOIN;
                */
            }
            response = new Packet(returnHeader, returnData);
            return response;
        }
        public Packet GetResponse(out ClientHandle surrogateClient)
        {
            Packet returnPacket;
            Header returnHeader = new Header();
            byte[] returnData = null;
            ClientHandle surrogateCandidate = null; //null is default

            bool debug = true;

            if(debug)
                Console.WriteLine("==RECEIVED: \n" + PacketDebug(recvPacket));

            //=============================COMM CS==============================
            if (Comm.CS == recvPacket.header.comm)
            {
                byte[] roomnameBytes;
                byte[] roomIdBytes;
                byte[] usernameBytes;
                byte[] passwordBytes;
                string username;
                string password;
                string roomname;
                long userId;
                long roomId = 0;
                bool createAndJoin = false;
                Room requestedRoom;
                Packet responsePacket;

                switch (recvPacket.header.code)
                {
                    //------------No action from client----------
                    case -1:
                        returnHeader = new Header(Comm.CS, Code.HEARTBEAT, 0);
                        returnData = null;
                        break;
                    //------------CREATE------------
                    case Code.CREATE:
                        //CL -> FE side
                        responsePacket = ResponseCreate(recvPacket, redis);

                        //temp
                        returnHeader = responsePacket.header;
                        returnData = responsePacket.data;
                        break;
                    //------------DESTROY------------
                    case Code.DESTROY:
                        //CL -> FE side
                        break;
                    //------------FAIL------------
                    case Code.FAIL:
                        returnHeader = NoResponseHeader;
                        returnData = null;
                        break;


                    //------------HEARTBEAT------------
                    case Code.HEARTBEAT:
                        //FE -> CL side
                        break;
                    case Code.HEARTBEAT_RES:
                        //CL -> FE side
                        returnHeader = NoResponseHeader;
                        returnData = null;
                        break;


                    //------------JOIN------------
                    case Code.JOIN:
                        //CL -> FE side
                        if (createAndJoin)
                            createAndJoin = false; //reuse roomId already set and set flag to false
                        else
                            roomId = ToInt64(recvPacket.data, 0);
                        
                        //TODO: allow client to join other room while in another room?
                        //      or just allow to join when client is in lobby?
                        
                        lock (rooms)
                        {
                            if (!rooms.TryGetValue(roomId, out requestedRoom))
                            {
                                byte[] sreqUserId = BitConverter.GetBytes(client.UserId);
                                byte[] sreqRoomId = BitConverter.GetBytes(roomId);
                                byte[] sreqData = new byte[sreqUserId.Length + sreqRoomId.Length];
                                Array.Copy(sreqUserId, 0, sreqData, 0, sreqUserId.Length);
                                Array.Copy(sreqRoomId, 0, sreqData, sreqUserId.Length, sreqRoomId.Length);

                                Header sreqHeader = new Header(Comm.SS, Code.SJOIN, sreqData.Length, (short)peerServers.Count);
                                Packet sreqPacket = new Packet(sreqHeader, sreqData);

                                if (peerServers.Count == 0)
                                {
                                    returnHeader = new Header(Comm.CS, Code.JOIN_NULL_ERR, 0);
                                    returnData = null;
                                }
                                else
                                {
                                    //put user into peerRespWait so when peer response arrives
                                    //there is a way to check if room doesn't exist.
                                    lock (peerRespWait)
                                        peerRespWait.Add(client.UserId, new int[] { peerServers.Count, 0, 0 });

                                    //room not in local server. check other servers
                                    foreach (ServerHandle peer in peerServers)
                                    {
                                        bool success = peer.Send(sreqPacket);

                                        if (!success)
                                            Console.WriteLine("ERROR: SJOIN send failed");
                                    }

                                    returnHeader = NoResponseHeader;
                                    returnData = null;
                                }
                            }
                            else
                            {
                                lobbyClients.Remove(client.UserId);
                                requestedRoom.AddClient(client);
                                client.Status = ClientHandle.State.Room;
                                client.RoomId = roomId;

                                roomIdBytes = BitConverter.GetBytes(roomId);
                                returnHeader = new Header(Comm.CS, Code.JOIN_RES, roomIdBytes.Length);
                                returnData = roomIdBytes;
                            }
                        }
                        break;
                    case Code.JOIN_FULL_ERR:
                        //FE -> CL side
                        break;
                    case Code.JOIN_NULL_ERR:
                        //FE -> CL side
                        break;


                    //------------LEAVE------------
                    case Code.LEAVE:
                        //CL -> FE side
                        if (lobbyClients.ContainsKey(client.UserId))
                        {
                            returnHeader = new Header(Comm.CS, Code.LEAVE_ERR, 0);
                            returnData = null;
                        }
                        else
                        {
                            bool roomEmpty = false;
                            lock (rooms)
                            {
                                if (!rooms.TryGetValue(client.RoomId, out requestedRoom))
                                {
                                    Console.WriteLine("ERROR: Client is in a room that doesn't exist. WTF you fucked up.");
                                    returnHeader = new Header(Comm.CS, Code.LEAVE_ERR, 0);
                                    returnData = null;
                                    break;
                                }
                                else
                                {
                                    requestedRoom.RemoveClient(client);
                                    client.RoomId = 0;
                                    client.Status = ClientHandle.State.Lobby;

                                    if(requestedRoom.Clients.Count == 0)
                                    {
                                        rooms.Remove(requestedRoom.RoomId);
                                        roomEmpty = true;

                                        returnHeader = new Header(Comm.CS, Code.LEAVE_RES, 0);
                                        returnData = null;
                                    }
                                }
                            }

                            lock (lobbyClients)
                                lobbyClients.Add(client.UserId, client);

                            if (roomEmpty)
                            {
                                Packet reqPacket;
                                Header reqHeader;
                                roomIdBytes = BitConverter.GetBytes(requestedRoom.RoomId);
                                reqHeader = new Header(Comm.SS, Code.SLEAVE, roomIdBytes.Length);
                                reqPacket.header = reqHeader;
                                reqPacket.data = roomIdBytes;

                                foreach (ServerHandle peerServer in requestedRoom.Servers)
                                    peerServer.EchoSend(reqPacket);
                            }
                        }
                        //remove clienthandle from room.clients
                        //check if clienthandle is empty
                        //if empty, send SDESTROY to servers in room.servers
                        break;
                    case Code.LEAVE_ERR:
                        //FE -> CL side
                        break;


                    //------------LIST------------
                    case Code.LIST:
                        //CL -> FE side
                        if (!lobbyClients.ContainsKey(client.UserId))
                            Console.WriteLine("you ain't in the lobby to list biatch");

                        byte[] slistUserId = BitConverter.GetBytes(client.UserId);
                        byte[] slistData = new byte[slistUserId.Length];

                        Header slistHeader = new Header(Comm.SS, Code.SLIST, slistUserId.Length, (short)(peerServers.Count + 1)); // +1, because need to include self because server that will give LIST_RES response
                        Packet slistPacket = new Packet(slistHeader, slistUserId);
                        int peerServerCount = 1; //start is 1, because need to include self as server that will give LIST_RES response
                        //room not in local server. check other servers
                        foreach (ServerHandle peer in peerServers)
                        {
                            bool success = peer.Send(slistPacket);

                            if (!success)
                                Console.WriteLine("ERROR: SJOIN send failed");
                            else
                                peerServerCount++;
                        }

                        string[] pairArr = new string[rooms.Count];
                        int length = 0;
                        int i = 0;
                        lock(rooms)
                        {
                            foreach (KeyValuePair<long, Room> entry in rooms)
                            {
                                string pair = entry.Key + ":" + entry.Value.Roomname + ";";
                                pairArr[i] = pair;
                                i++;
                                length += Encoding.UTF8.GetByteCount(pair);
                            }
                        }

                        byte[] listBytes = new byte[length];
                        int prev = 0;
                        for (int j = 0; j < pairArr.Length; j++)
                        {
                            byte[] pairBytes;
                            string pair = pairArr[j];
                            if (j == pairArr.Length - 1)
                                pairBytes = Encoding.UTF8.GetBytes(pair.Substring(0, pair.Length - 1)); //remove the last semicolon
                            else
                                pairBytes = Encoding.UTF8.GetBytes(pair);
                            
                            if (debug)
                                Console.WriteLine("=============list  " + Encoding.UTF8.GetString(pairBytes));
                            Array.Copy(pairBytes, 0, listBytes, prev, pairBytes.Length);
                            prev += pairBytes.Length;
                        }

                        returnHeader = new Header(Comm.CS, Code.LIST_RES, listBytes.Length, (short)peerServerCount);
                        returnData = listBytes;
                        break;
                    case Code.LIST_ERR:
                        //FE -> CL side
                        break;
                    case Code.LIST_RES:
                        //FE -> CL side
                        break;


                    //------------MSG------------
                    case Code.MSG:
                        //CL <--> FE side
                        //TODO: update user chat count. make it so that it increments value in redis
                        client.ChatCount++;
                        redis.IncrementUserChatCount(client.UserId);
                        client.ChatCount = 0;

                        lock(rooms)
                        {
                            if(!rooms.TryGetValue(client.RoomId, out requestedRoom))
                            {
                                Console.WriteLine("ERROR: Msg - Room doesn't exist");
                                // room doesnt exist error
                            }
                            else
                            {
                                foreach (ClientHandle peerClient in requestedRoom.Clients)
                                    peerClient.EchoSend(recvPacket);

                                recvPacket.header.comm = Comm.SS;
                                recvPacket.header.code = Code.SMSG;
                                foreach (ServerHandle peerServer in requestedRoom.Servers)
                                    peerServer.EchoSend(recvPacket);

                                returnHeader = NoResponseHeader;
                                returnData = null;
                            }
                        }
                        break;
                    case Code.MSG_ERR:
                        //CL <--> FE side
                        break;


                    //------------SIGNIN------------
                    case Code.SIGNIN:
                        //CL -> FE -> BE side
                        //bytes to string
                        usernameBytes = new byte[12];
                        passwordBytes = new byte[18];
                        Array.Copy(recvPacket.data, 0, usernameBytes, 0, 12);
                        Array.Copy(recvPacket.data, 12, passwordBytes, 0, 18);

                        username = Encoding.UTF8.GetString(usernameBytes).Trim();
                        password = Encoding.UTF8.GetString(passwordBytes).Trim();
                        userId = redis.SignIn(username, password);

                        if (-1 == userId)
                        {
                            //make packet for signin error
                            returnHeader = new Header(Comm.CS, Code.SIGNIN_ERR, 0);
                            returnData = null;
                        }
                        else
                        {
                            client.UserId = userId;
                            client.Status = ClientHandle.State.Lobby;
                            lobbyClients.Add(userId, client);
                            //make packet for signin success
                            returnHeader = new Header(Comm.CS, Code.SIGNIN_RES, 0);
                            returnData = null;
                        }
                        break;
                    case Code.SIGNIN_ERR:
                        //BE -> FE -> CL side
                        break;
                    case Code.SIGNIN_RES:
                        //BE -> FE -> CL side
                        break;
                    case Code.SIGNIN_DUMMY:
                        //CL -> FE
                        userId = redis.CreateDummy();
                        userId = redis.SignInDummy(userId);
                        client.IsDummy = true;
                        client.UserId = userId;
                        client.Status = ClientHandle.State.Lobby;
                        lobbyClients.Add(userId, client);
                        //make packet for signin success
                        returnHeader = new Header(Comm.CS, Code.SIGNIN_RES, 0);
                        returnData = null;
                        break;


                    //------------SIGNUP------------
                    case Code.SIGNUP:
                        //CL -> FE -> BE side
                        //bytes to string
                        usernameBytes = new byte[12];
                        passwordBytes = new byte[18];
                        Array.Copy(recvPacket.data, 0, usernameBytes, 0, 12);
                        Array.Copy(recvPacket.data, 12, passwordBytes, 0, 18);

                        username = Encoding.UTF8.GetString(usernameBytes).Trim();
                        password = Encoding.UTF8.GetString(passwordBytes).Trim();
                        userId = redis.CreateUser(username, password);

                        if (-1 == userId)
                        {
                            //make packet for signup error
                            returnHeader = new Header(Comm.CS, Code.SIGNUP_ERR, 0);
                            returnData = null;
                        }
                        else
                        {
                            client.UserId = userId;
                            //make packet for signup success
                            returnHeader = new Header(Comm.CS, Code.SIGNUP_RES, 0);
                            returnData = null;
                        }
                        break;
                    case Code.SIGNUP_ERR:
                        //BE -> FE -> CL side
                        //error handling
                        break;
                    case Code.SIGNUP_RES:
                        //BE -> FE -> CL side
                        //success
                        break;


                    //------------SUCCESS------------
                    case Code.SUCCESS:
                        //
                        break;

                    default:
                        if(debug)
                            Console.WriteLine("Unknown code: {0}\n", recvPacket.header.code);
                        break;
                }
                surrogateCandidate = client;
            }
            //=============================COMM SS==============================
            else if (Comm.SS == recvPacket.header.comm)
            {
                Room requestedRoom;
                long userId;
                long recvRoomId;
                string recvRoomname;
                string roomname = "";
                byte[] roomIdBytes;
                byte[] roomnameBytes;

                switch (recvPacket.header.code)
                {
                    //------------No action from client----------
                    case -1:
                        returnHeader = new Header(Comm.SS, Code.HEARTBEAT, 0);
                        returnData = null;
                        break;

                    //------------HEARTBEAT------------
                    case Code.HEARTBEAT:
                        //FE -> CL side
                        returnHeader = new Header(Comm.SS, Code.HEARTBEAT_RES, 0);
                        returnData = null;
                        break;
                    case Code.HEARTBEAT_RES:
                        //CL -> FE side
                        returnHeader = NoResponseHeader;
                        returnData = null;
                        break;

                    //------------SDESTROY------------
                    case Code.SDESTROY:
                        //FE side
                        break;
                    case Code.SDESTROY_ERR:
                        //FE side
                        break;


                    //------------SJOIN------------
                    case Code.SJOIN:
                        //FE side
                        //receive UserID (8byte) + RoomId (8byte)
                        recvRoomId = ToInt64(recvPacket.data, 8);
                        bool haveRoom;
                        lock (rooms)
                        {
                            haveRoom = rooms.ContainsKey(recvRoomId);
                            if(haveRoom)
                            {
                                if (rooms.TryGetValue(recvRoomId, out requestedRoom))
                                {
                                    requestedRoom.AddServer(server);
                                    roomname = requestedRoom.Roomname;
                                }
                            }
                        }
                        
                        if(haveRoom)
                        {
                            roomnameBytes = Encoding.UTF8.GetBytes(roomname);
                            byte[] respBytes = new byte[recvPacket.data.Length + roomnameBytes.Length];
                            Array.Copy(recvPacket.data, 0, respBytes, 0, 16);
                            Array.Copy(roomnameBytes, 0, respBytes, 16, roomnameBytes.Length);
                            returnHeader = new Header(Comm.SS, Code.SJOIN_RES, respBytes.Length, recvPacket.header.sequence);
                            returnData = respBytes; //need to send back room id. so receiver can check again if they are the same id
                        }
                        else
                        {
                            returnHeader = new Header(Comm.SS, Code.SJOIN_ERR, recvPacket.data.Length, recvPacket.header.sequence);
                            returnData = recvPacket.data;
                        }
                        //response should include roomid
                        break;
                    case Code.SJOIN_RES:
                        //FE side
                        //need to get 'client' because this function will return to
                        //a ServerHandle instance, which will have a SS socket
                        //so need to set surrogateClient so that the returnPacket is 
                        //send through the surrogateClient
                        userId = ToInt64(recvPacket.data, 0);
                        recvRoomId = ToInt64(recvPacket.data, 8);
                        roomnameBytes = new byte[recvPacket.data.Length - 16]; //16 because 8bytes for userid, 8bytes for roomid
                        Array.Copy(recvPacket.data, 16, roomnameBytes, 0, roomnameBytes.Length);
                        recvRoomname = Encoding.UTF8.GetString(roomnameBytes);
                        
                        lock(lobbyClients)
                        {
                            if (!lobbyClients.TryGetValue(userId, out client))
                            {
                                Console.WriteLine("ERROR: SJOIN_RES - user no longer exists");
                                returnHeader = NoResponseHeader;
                                returnData = null;
                                break;
                            }
                        }

                        lock(rooms)
                        {
                            if (!rooms.TryGetValue(recvRoomId, out requestedRoom))
                            {
                                //first SJOIN_RES from peer servers
                                Room newJoinRoom = new Room(recvRoomId, recvRoomname);
                                lobbyClients.Remove(client.UserId);
                                newJoinRoom.AddClient(client);
                                newJoinRoom.AddServer(server);

                                rooms.Add(recvRoomId, newJoinRoom);

                                client.Status = ClientHandle.State.Room;
                                client.RoomId = recvRoomId;
                                
                                //only the first SJOIN_RES will give the client a JOIN_RES response
                                //the others will use NoResponseHeader (see 'else' returnHeader assignment)
                                roomIdBytes = BitConverter.GetBytes(recvRoomId);
                                returnHeader = new Header(Comm.CS, Code.JOIN_RES, roomIdBytes.Length);
                                returnData = roomIdBytes;
                            }
                            else
                            {
                                //room already made by previous iteration
                                requestedRoom.AddServer(server);

                                //only the first SJOIN_RES will give the client a JOIN_RES response
                                //the others will use NoResponseHeader (see 'if' returnHeader assignment)
                                returnHeader = NoResponseHeader;
                                returnData = null;
                            }
                        }

                        lock (peerRespWait)
                        {
                            int[] respProgress;
                            if (!peerRespWait.TryGetValue(client.UserId, out respProgress))
                            {
                                Console.WriteLine("ERROR: SJOIN_RES - respProgress no longer exists");
                                returnHeader = NoResponseHeader;
                                returnData = null;
                            }

                            respProgress[1]++; //increment progress
                            respProgress[2] = 1; //set join success to true
                            if (respProgress[0] == respProgress[1])
                                peerRespWait.Remove(client.UserId);
                        }

                        //room exists in other peer irc servers
                        //created one in local and connected with peers
                        surrogateCandidate = client;
                        break;
                    case Code.SJOIN_ERR:
                        //FE side
                        //no such key exists error (no such room error)
                        //need to get 'client' because this function will return to
                        //a ServerHandle instance, which will have a SS socket
                        //so need to set surrogateClient so that the returnPacket is 
                        //send through the surrogateClient
                        userId = ToInt64(recvPacket.data, 0);
                        recvRoomId = ToInt64(recvPacket.data, 8);

                        lock (lobbyClients)
                        {
                            if (!lobbyClients.TryGetValue(userId, out client))
                            {
                                Console.WriteLine("ERROR: SJOIN_RES - user no longer exists");
                                returnHeader = NoResponseHeader;
                                returnData = null;
                                break;
                            }
                        }

                        lock (peerRespWait)
                        {
                            int[] respProgress;
                            if (!peerRespWait.TryGetValue(client.UserId, out respProgress))
                            {
                                Console.WriteLine("ERROR: SJOIN_RES - respProgress no longer exists");
                                returnHeader = NoResponseHeader;
                                returnData = null;
                            }

                            respProgress[1]++; //increment progress
                            respProgress[2] = respProgress[2] == 1 ? 1 : 0; //set join success to true
                            if (respProgress[0] == respProgress[1] && respProgress[2] == 0)
                            {
                                peerRespWait.Remove(client.UserId);
                                returnHeader = new Header(Comm.CS, Code.JOIN_NULL_ERR, 0);
                                returnData = null;
                            }
                            else
                            {
                                returnHeader = NoResponseHeader;
                                returnData = null;
                            }
                        }

                        surrogateCandidate = client;
                        break;

                    //------------SLEAVE-----------
                    case Code.SLEAVE:
                        //FE side
                        recvRoomId = ToInt64(recvPacket.data, 0);

                        lock (rooms)
                        {
                            if (rooms.ContainsKey(recvRoomId))
                            {
                                if (rooms.TryGetValue(recvRoomId, out requestedRoom))
                                    requestedRoom.RemoveServer(server);
                            }
                        }

                        //returnHeader = new Header(Comm.SS, Code.SLEAVE_RES, 0);
                        returnHeader = NoResponseHeader;
                        returnData = null;
                        break;
                    case Code.SLEAVE_ERR:
                        //FE side
                        break;
                    case Code.SLEAVE_RES:
                        //FE side
                        break;

                    //------------SLIST------------
                    case Code.SLIST:
                        //FE side
                        string[] pairArr = new string[rooms.Count];
                        int length = 0;
                        int i = 0;
                        lock (rooms)
                        {
                            foreach (KeyValuePair<long, Room> entry in rooms)
                            {
                                string pair = entry.Key + ":" + entry.Value.Roomname + ";";
                                pairArr[i] = pair;
                                i++;
                                length += Encoding.UTF8.GetByteCount(pair);
                            }
                        }
                        //userId is in recvPacket.data. need to forward it back so that
                        //the receiver knows which connection needs to receive the response
                        byte[] listBytes = new byte[length + recvPacket.data.Length];
                        Array.Copy(recvPacket.data, 0, listBytes, 0, recvPacket.data.Length);
                        int prev = recvPacket.data.Length;
                        foreach (string pair in pairArr)
                        {
                            byte[] pairBytes = Encoding.UTF8.GetBytes(pair);
                            Array.Copy(pairBytes, 0, listBytes, prev, pairBytes.Length);
                            prev += pairBytes.Length;
                        }

                        returnHeader = new Header(Comm.SS, Code.SLIST_RES, listBytes.Length, recvPacket.header.sequence);
                        returnData = listBytes;
                        break;
                    case Code.SLIST_ERR:
                        //FE side
                        break;
                        
                    case Code.SLIST_RES:
                        //FE side
                        userId = ToInt64(recvPacket.data, 0);

                        lock (lobbyClients)
                        {
                            if (!lobbyClients.TryGetValue(userId, out client))
                                Console.WriteLine("ERROR: SJOIN_RES - user no longer exists");
                        }

                        surrogateCandidate = client;
                        byte[] clientListBytes = new byte[recvPacket.data.Length - 8];
                        Array.Copy(recvPacket.data, 8, clientListBytes, 0, clientListBytes.Length);
                        returnHeader = new Header(Comm.CS, Code.LIST_RES, clientListBytes.Length, recvPacket.header.sequence);
                        returnData = clientListBytes;
                        break;
                        

                    //------------SMSG------------                
                    case Code.SMSG:
                        //FE side
                        roomIdBytes = new byte[8];
                        Array.Copy(recvPacket.data, 0, roomIdBytes, 0, 8);
                        long roomId = ToInt64(roomIdBytes, 0);
                        
                        lock (rooms)
                        {
                            if (!rooms.TryGetValue(roomId, out requestedRoom))
                            {
                                Console.WriteLine("ERROR: SMSG - room doesn't exist {0}", roomId);
                            }
                            else
                            {
                                foreach (ClientHandle peerClient in requestedRoom.Clients)
                                    peerClient.EchoSend(recvPacket);

                                returnHeader = NoResponseHeader;
                                returnData = null;
                            }
                        }
                        break;
                    case Code.SMSG_ERR:
                        //FE side
                        break;
                }
            }
            //=============================COMM DS==============================
            else if (Comm.DUMMY == recvPacket.header.comm)
            {
                
            }

            //===============Build Response/Set Surrogate/Return================
            returnPacket = new Packet(returnHeader, returnData);
            if (debug && returnPacket.header.comm != -1)
                Console.WriteLine("==SEND: \n" + PacketDebug(returnPacket));

            surrogateClient = surrogateCandidate;
            return returnPacket;
        }
        
        private long ToInt64(byte[] bytes, int startIndex)
        {
            long result = 0;
            try
            {
                result = BitConverter.ToInt64(bytes, startIndex);
            }
            catch (Exception)
            {
                Console.WriteLine("bytes to int64: fuck you. you messsed up");
            }

            return result;
        }

        private Socket ConnectToPeer(string info)
        {
            string host;
            int port;

            string[] hostport = info.Split(':');
            host = hostport[0];
            if (!int.TryParse(hostport[1], out port))
            {
                Console.Error.WriteLine("port must be int. given: {0}", hostport[1]);
                Environment.Exit(0);
            }

            Socket so = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPAddress ipAddress = IPAddress.Parse(host);
            Console.WriteLine("[Server] Establishing connection to {0}:{1} ...", host, port);

            try
            {
                so.Connect(ipAddress, port);
                //Console.WriteLine("[Server] Connection established.\n");
            }
            catch(Exception e)
            {
                Console.WriteLine("Peer is not alive.");
                return null;
            }

            return so;
        }
    }
}
