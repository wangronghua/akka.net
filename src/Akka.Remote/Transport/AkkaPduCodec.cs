﻿using System;
using Akka.Actor;
using Google.ProtocolBuffers;

namespace Akka.Remote.Transport
{
    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal class PduCodecException : AkkaException
    {
        public PduCodecException(string msg, Exception cause = null) : base(msg, cause) { }
    }

    /*
     * Interface used to represent Akka PDUs (Protocol Data Unit)
     */
    internal interface IAkkaPdu { }

    internal sealed class Associate : IAkkaPdu
    {
        public Associate(HandshakeInfo info)
        {
            Info = info;
        }

        public HandshakeInfo Info { get; private set; }
    }

    internal sealed class Disassociate : IAkkaPdu
    {
        public Disassociate(DisassociateInfo reason)
        {
            Reason = reason;
        }

        public DisassociateInfo Reason { get; private set; }
    }
    internal sealed class Heartbeat : IAkkaPdu { }

    internal sealed class Payload : IAkkaPdu
    {
        public Payload(ByteString bytes)
        {
            Bytes = bytes;
        }

        public ByteString Bytes { get; private set; }
    }

    internal sealed class Message : IAkkaPdu, IHasSequenceNumber
    {
        public Message(InternalActorRef recipient, Address recipientAddress, SerializedMessage serializedMessage, ActorRef senderOptional = null, SeqNo seq = null)
        {
            Seq = seq;
            SenderOptional = senderOptional;
            SerializedMessage = serializedMessage;
            RecipientAddress = recipientAddress;
            Recipient = recipient;
        }

        public InternalActorRef Recipient { get; private set; }

        public Address RecipientAddress { get; private set; }

        public SerializedMessage SerializedMessage { get; private set; }

        public ActorRef SenderOptional { get; private set; }

        public bool ReliableDeliveryEnabled { get { return Seq != null; } }

        public SeqNo Seq { get; private set; }
    }

    /// <summary>
    /// INTERNAL API
    /// 
    /// A Codec that is able to convert Akka PDUs from and to <see cref="ByteString"/>
    /// </summary>
    internal abstract class AkkaPduCodec
    {
        /// <summary>
        /// Return an <see cref="IAkkaPdu"/> instance that represents a PDU contained in the raw
        /// <see cref="ByteString"/>.
        /// </summary>
        /// <param name="raw">Encoded raw byte representation of an Akka PDU</param>
        /// <returns>Class representation of a PDU that can be used in a <see cref="PatternMatch"/>.</returns>
        public abstract IAkkaPdu DecodePdu(ByteString raw);

        /// <summary>
        /// Takes an <see cref="IAkkaPdu"/> representation of an Akka PDU and returns its encoded form
        /// as a <see cref="ByteString"/>.
        /// </summary>
        /// <param name="pdu"></param>
        /// <returns></returns>
        public virtual ByteString EncodePdu(IAkkaPdu pdu)
        {
            ByteString finalBytes = null;
            pdu.Match()
                .With<Associate>(a => finalBytes = ConstructAssociate(a.Info))
                .With<Payload>(p => finalBytes = ConstructPayload(p.Bytes))
                .With<Disassociate>(d => finalBytes = ConstructDisassociate(d.Reason))
                .With<Heartbeat>(h => finalBytes = ConstructHeartbeat());

            return finalBytes;
        }

        public abstract ByteString ConstructPayload(ByteString payload);

        public abstract ByteString ConstructAssociate(HandshakeInfo info);

        public abstract ByteString ConstructDisassociate(DisassociateInfo reason);

        public abstract ByteString ConstructHeartbeat();

        public abstract Message DecodeMessage(ByteString raw, RemoteActorRefProvider provider, Address localAddress);

        public abstract ByteString ConstructMessage(Address localAddress, ActorRef recipient,
            SerializedMessage serializedMessage, ActorRef senderOption = null);
    }

    internal class AkkaPduProtobuffCodec : AkkaPduCodec
    {
        public override IAkkaPdu DecodePdu(ByteString raw)
        {
            try
            {
                var pdu = AkkaProtocolMessage.ParseFrom(raw);
                if(pdu.HasPayload) return new Payload(pdu.Payload);
                else if (pdu.HasInstruction) return DecodeControlPdu(pdu.Instruction);
                else throw new PduCodecException("Error decoding Akka PDU: Neither message nor control message were contained");
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new PduCodecException("Decoding PDU failed", ex);
            }
        }

        public override ByteString ConstructPayload(ByteString payload)
        {
            return AkkaProtocolMessage.CreateBuilder().SetPayload(payload).Build().ToByteString();
        }

        public override ByteString ConstructAssociate(HandshakeInfo info)
        {
            var handshakeInfo = AkkaHandshakeInfo.CreateBuilder()
                .SetOrigin(SerializeAddress(info.Origin))
                .SetUid((ulong)info.Uid);

            return ConstructControlMessagePdu(CommandType.ASSOCIATE, handshakeInfo);
        }

        public override ByteString ConstructDisassociate(DisassociateInfo reason)
        {
            switch (reason)
            {
                case DisassociateInfo.Quarantined:
                    return DISASSOCIATE_QUARANTINED;
                case DisassociateInfo.Shutdown:
                    return DISASSOCIATE_SHUTTING_DOWN;
                case DisassociateInfo.Unknown:
                default:
                    return DISASSOCIATE;
            }
        }

        public override ByteString ConstructHeartbeat()
        {
            return ConstructControlMessagePdu(CommandType.HEARTBEAT);
        }

        public override Message DecodeMessage(ByteString raw, RemoteActorRefProvider provider, Address localAddress)
        {
            var envelopeContainer = RemoteEnvelope.ParseFrom(raw);
            if (envelopeContainer != null)
            {
                var recipient = provider.ResolveActorRefWithLocalAddress(envelopeContainer.Recipient.Path, localAddress);
                var recipientAddress = ActorPath.Parse(envelopeContainer.Recipient.Path).Address;
                var serializedMessage = envelopeContainer.Message;
                ActorRef senderOption = null;
                if (envelopeContainer.HasSender)
                {
                    senderOption = provider.ResolveActorRefWithLocalAddress(envelopeContainer.Sender.Path, localAddress);
                }
                return new Message(recipient, recipientAddress, serializedMessage, senderOption);
            }

            return null;
        }

        public override ByteString ConstructMessage(Address localAddress, ActorRef recipient, SerializedMessage serializedMessage,
            ActorRef senderOption = null)
        {
            var envelopeBuilder = RemoteEnvelope.CreateBuilder().SetRecipient(SerializeActorRef(recipient.Path.Address, recipient));
            if (senderOption != null) { envelopeBuilder = envelopeBuilder.SetSender(SerializeActorRef(localAddress, senderOption)); }
            envelopeBuilder = envelopeBuilder.SetMessage(serializedMessage);

            return envelopeBuilder.Build().ToByteString();
        }

        #region Internal methods
        private IAkkaPdu DecodeControlPdu(AkkaControlMessage controlPdu)
        {
            switch (controlPdu.CommandType)
            {
                case CommandType.ASSOCIATE:
                    if (controlPdu.HasHandshakeInfo)
                    {
                        var handshakeInfo = controlPdu.HandshakeInfo;
                        return new Associate(new HandshakeInfo(DecodeAddress(handshakeInfo.Origin), (int)handshakeInfo.Uid));
                    }
                    break;
                case CommandType.DISASSOCIATE:
                    return new Disassociate(DisassociateInfo.Unknown);
                case CommandType.DISASSOCIATE_QUARANTINED:
                    return new Disassociate(DisassociateInfo.Quarantined);
                case CommandType.DISASSOCIATE_SHUTTING_DOWN:
                    return new Disassociate(DisassociateInfo.Shutdown);
                case CommandType.HEARTBEAT:
                    return new Heartbeat();
            }

            throw new PduCodecException(string.Format("Decoding of control PDU failed, invalid format, unexpected {0}", controlPdu));
        }



        private ByteString DISASSOCIATE
        {
            get { return ConstructControlMessagePdu(CommandType.DISASSOCIATE); }
        }

        private ByteString DISASSOCIATE_SHUTTING_DOWN
        {
            get { return ConstructControlMessagePdu(CommandType.DISASSOCIATE_SHUTTING_DOWN); }
        }

        private ByteString DISASSOCIATE_QUARANTINED
        {
            get { return ConstructControlMessagePdu(CommandType.DISASSOCIATE_QUARANTINED); }
        }

        private ByteString ConstructControlMessagePdu(CommandType code, AkkaHandshakeInfo.Builder handshakeInfo = null)
        {
            var controlMessageBuilder = AkkaControlMessage.CreateBuilder()
                .SetCommandType(code);
            if (handshakeInfo != null)
            {
                controlMessageBuilder = controlMessageBuilder.SetHandshakeInfo(handshakeInfo);
            }

            return
                AkkaProtocolMessage.CreateBuilder().SetInstruction(controlMessageBuilder.Build()).Build().ToByteString();
        }

        private Address DecodeAddress(AddressData origin)
        {
            return new Address(origin.Protocol, origin.System, origin.Hostname, (int)origin.Port);
        }

        private ActorRefData SerializeActorRef(Address defaultAddress, ActorRef actorRef)
        {
            return ActorRefData.CreateBuilder()
                .SetPath(!string.IsNullOrEmpty(actorRef.Path.Address.Host)
                    ? actorRef.Path.ToSerializationFormat()
                    : actorRef.Path.ToStringWithAddress(defaultAddress))
                .Build();
        }

        private AddressData SerializeAddress(Address address)
        {
            if (string.IsNullOrEmpty(address.Host) || !address.Port.HasValue) throw new ArgumentException(string.Format("Address {0} could not be serialized: host or port missing", address));
            return AddressData.CreateBuilder()
                .SetHostname(address.Host)
                .SetPort((uint)address.Port.Value)
                .SetSystem(address.System)
                .SetProtocol(address.Protocol)
                .Build();
        }

        #endregion
    }
}
