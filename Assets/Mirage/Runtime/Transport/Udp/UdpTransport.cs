﻿using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;

namespace Mirage.UDP
{
    public class UdpTransport : Transport
    {
        public override IEnumerable<string> Scheme => throw new NotImplementedException();

        public override bool Supported => throw new NotImplementedException();

        public override UniTask<IConnection> ConnectAsync(Uri uri)
        {
            throw new NotImplementedException();
        }

        public override IConnection CreateClientConnection()
        {
            return new UdpConnection();
        }

        public override IConnection CreateServerConnection()
        {
            return new UdpConnection();
        }

        public override void Disconnect()
        {
            throw new NotImplementedException();
        }

        public override IEnumerable<Uri> ServerUri()
        {
            throw new NotImplementedException();
        }
    }
}