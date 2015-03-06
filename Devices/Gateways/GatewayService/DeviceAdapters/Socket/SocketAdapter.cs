﻿//  ---------------------------------------------------------------------------------
//  Copyright (c) Microsoft Open Technologies, Inc.  All rights reserved.
// 
//  The MIT License (MIT)
// 
//  Permission is hereby granted, free of charge, to any person obtaining a copy
//  of this software and associated documentation files (the "Software"), to deal
//  in the Software without restriction, including without limitation the rights
//  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//  copies of the Software, and to permit persons to whom the Software is
//  furnished to do so, subject to the following conditions:
// 
//  The above copyright notice and this permission notice shall be included in
//  all copies or substantial portions of the Software.
// 
//  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//  THE SOFTWARE.
//  ---------------------------------------------------------------------------------

namespace Microsoft.ConnectTheDots.Adapters
{
    using System;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.ConnectTheDots.Common;
    using Microsoft.ConnectTheDots.Gateway;

    //--//

    public class SocketAdapter : DeviceAdapterAbstract
    {
        private const int CONNECTION_RETRIES         = 20000;
        private const int SLEEP_TIME_BETWEEN_RETRIES = 1000; // 1 sec

        //--//

        private Func<string, int>   _enqueue;
        private bool                _doWorkSwitch;
        private Thread              _listeningThread;
        private SensorEndpoint      _endpoint;

        //--//

        public SocketAdapter( ILogger logger )
            : base( logger )
        {
        }

        public override bool Start( Func<string, int> enqueue )
        {
            _enqueue = enqueue;

            _doWorkSwitch = true;

            var sh = new SafeAction<int>( ( t ) => RunForSocket( t ), _logger );

            Task.Run( ( ) => sh.SafeInvoke( CONNECTION_RETRIES ) );

            return true;
        }

        public override bool Stop( )
        {
            _doWorkSwitch = false;

            return true;
        }

        public override bool SetEndpoint( SensorEndpoint endpoint = null )
        {
            if( endpoint == null )
            {
                //we need to know endpoint
                return false;
            }

            _endpoint = endpoint;

            return true;
        }

        private int RunForSocket( int retries )
        {
            int step = retries;

            while( --step > 0 && _doWorkSwitch )
            {
                try
                {
                    _logger.LogInfo( "Try connecting to device - step: " + ( CONNECTION_RETRIES - step ) );

                    Socket client = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Unspecified );

                    client.Connect( _endpoint.Host, _endpoint.Port );

                    if( client.Connected )
                    {
                        _logger.LogInfo( string.Format( "Socket connected to {0}", client.RemoteEndPoint.ToString() ) );

                        _listeningThread = new Thread( ( ) => SensorDataClient( client ) );
                        _listeningThread.Start( );

                        _logger.LogInfo( string.Format( "Reader thread started" ) );

                        _listeningThread.Join( );

                        _logger.LogInfo( "Listening thread terminated. Quitting." );

                        //reset number of retries to connect
                        step = retries;
                    }
                    else
                    {
                        _logger.LogError( "No sensor connection detected. Quitting." );
                    }
                }
                catch( Exception ex )
                {
                    _logger.LogError( "Exception when opening socket:" + ex.StackTrace );
                    _logger.LogError( "Will retry in 1 second" );
                }

                // wait and try again
                Thread.Sleep( SLEEP_TIME_BETWEEN_RETRIES );
            }

            return 0;
        }

        public void SensorDataClient( Socket client )
        {
            try
            {
                StringBuilder jsonBuilder = new StringBuilder( );
                byte[] buffer = new Byte[ 1024 ];
                // Use Regular Expressions (Regex) to parse incoming data, which may contain multiple JSON strings 
                // USBSPLSOCKET.PY uses "<" and ">" to terminate JSON string at each end, so built Regex to find strings surrounded by angle brackets
                // You can test Regex extractor against a known string using a variety of online tools, such as http://regexhero.net/tester/ for C#.
                //Regex dataExtractor = new Regex(@"<(\d+.?\d*)>");
                Regex dataExtractor = new Regex( "<([\\w\\s\\d:\",-{}.]+)>" );

                while( _doWorkSwitch )
                {
                    try
                    {
                        if( !client.Connected )
                        {
                            client.Close( );
                            break;
                        }
                        int bytesRec = client.Receive( buffer );
                        int matchCount = 1;
                        // Read string from buffer
                        string data = Encoding.ASCII.GetString( buffer, 0, bytesRec );
                        //logger.Info("Read string: " + data);
                        if( data.Length > 0 )
                        {
                            // Parse string into angle bracket surrounded JSON strings
                            var matches = dataExtractor.Matches( data );
                            if( matches.Count >= 1 )
                            {
                                foreach( Match m in matches )
                                {
                                    jsonBuilder.Clear( );
                                    // Remove angle brackets
                                    //jsonBuilder.Append("{\"dspl\":\"Wensn Digital Sound Level Meter\",\"Subject\":\"sound\",\"DeviceGUID\":\"81E79059-A393-4797-8A7E-526C3EF9D64B\",\"decibels\":");
                                    jsonBuilder.Append( m.Captures[ 0 ].Value.Trim( ).Substring( 1, m.Captures[ 0 ].Value.Trim( ).Length - 2 ) );
                                    //jsonBuilder.Append("}");
                                    string jsonString = jsonBuilder.ToString( );
                                    //logger.Info("About to call SendAMQPMessage with JSON string: " + jsonString);
                                    _enqueue( jsonString );

                                    matchCount++;
                                }
                            }
                        }
                    }
                    catch( Exception ex )
                    {
                        _logger.LogError( "Exception processing data from socket: " + ex.StackTrace );
                        _logger.LogError( "Continuing..." );
                    }
                }
            }
            catch( StackOverflowException ex )
            {
                _logger.LogError( "Stack Overflow while processing data from socket: " + ex.StackTrace );
                _logger.LogError( "Closing program..." );

                throw;
            }
            catch( OutOfMemoryException ex )
            {
                _logger.LogError( "Stack Overflow while processing data from socket: " + ex.StackTrace );
                _logger.LogError( "Closing program..." );

                throw;
            }
            catch( SocketException ex )
            {
                _logger.LogError( "Socket exception processing data from socket: " + ex.StackTrace + ex.Message );
                _logger.LogError( "Continuing..." );

                // Dinar: this will raise every time when sensor stopped connection
                // wont throw to not stop service
                //throw;
            }
        }
    }
}
