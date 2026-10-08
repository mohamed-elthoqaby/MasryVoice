FROM debian:bookworm-slim

# Install Asterisk 20 and AudioSocket module
RUN apt-get update && apt-get install -y --no-install-recommends \
    asterisk \
    asterisk-modules \
    ca-certificates \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Copy configuration files
COPY pjsip.conf /etc/asterisk/pjsip.conf
COPY extensions.conf /etc/asterisk/extensions.conf
COPY audiosocket.conf /etc/asterisk/audiosocket.conf

# Expose SIP (UDP 5060) and RTP range (UDP 10000-10100)
EXPOSE 5060/udp
EXPOSE 10000-10100/udp

ENTRYPOINT ["/usr/sbin/asterisk", "-f", "-vvvg", "-c"]
