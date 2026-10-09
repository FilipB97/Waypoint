#!/bin/bash
# Testy integracyjne WaypointCore na Linuksie (CI, kontener jako root): prawdziwy OpenSSH (SFTP,
# bezpieczny zapis) i vsftpd (FTP, FTPS jawne i niejawne). Lokalnie: uruchamiać tylko w kontenerze.
set -euo pipefail
MAC_DIR="$(cd "$(dirname "$0")/.." && pwd)"
W="$(mktemp -d /tmp/wpint.XXXX)"

apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq openssh-server openssh-client vsftpd libcurl4-openssl-dev openssl sudo >/dev/null

PW="Wp-$(openssl rand -hex 6)"
useradd -m -s /bin/bash wpt
echo "wpt:$PW" | chpasswd
echo 'wpt ALL=(ALL) ALL' > /etc/sudoers.d/wpt; chmod 440 /etc/sudoers.d/wpt   # zapis przez sudo

# --- sshd na 127.0.0.1:2222 (klucz)
ssh-keygen -q -t ed25519 -N '' -f "$W/hostkey"
ssh-keygen -q -t ed25519 -N '' -f "$W/clientkey"
mkdir -p /home/wpt/.ssh /run/sshd
cp "$W/clientkey.pub" /home/wpt/.ssh/authorized_keys
chown -R wpt:wpt /home/wpt/.ssh; chmod 700 /home/wpt/.ssh; chmod 600 /home/wpt/.ssh/authorized_keys
cat > "$W/sshd_config" <<CFG
Port 2222
ListenAddress 127.0.0.1
HostKey $W/hostkey
PidFile $W/sshd.pid
PubkeyAuthentication yes
PasswordAuthentication no
UsePAM yes
Subsystem sftp /usr/lib/openssh/sftp-server
CFG
/usr/sbin/sshd -f "$W/sshd_config"

# --- vsftpd: zwykły (2122), FTPS jawne (2121), FTPS niejawne (2990); certyfikat samopodpisany
mkdir -p /var/run/vsftpd/empty
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$W/key.pem" -out "$W/cert.pem" -days 2 -subj "/CN=localhost" 2>/dev/null
BASE="listen=YES
listen_address=127.0.0.1
anonymous_enable=NO
local_enable=YES
write_enable=YES
local_umask=022
pasv_enable=YES
pasv_min_port=40000
pasv_max_port=40100
seccomp_sandbox=NO
secure_chroot_dir=/var/run/vsftpd/empty
pam_service_name=vsftpd"
TLS="rsa_cert_file=$W/cert.pem
rsa_private_key_file=$W/key.pem
force_local_logins_ssl=YES
force_local_data_ssl=YES
require_ssl_reuse=YES"
printf "%s\nlisten_port=2122\nssl_enable=NO\n" "$BASE" > "$W/plain.conf"
printf "%s\nlisten_port=2121\nssl_enable=YES\n%s\n" "$BASE" "$TLS" > "$W/explicit.conf"
printf "%s\nlisten_port=2990\nssl_enable=YES\nimplicit_ssl=YES\n%s\n" "$BASE" "$TLS" > "$W/implicit.conf"
for c in plain explicit implicit; do vsftpd "$W/$c.conf" & done
sleep 1

export WAYPOINT_SFTP_TEST="127.0.0.1:2222:wpt:$W/clientkey:$W/known_hosts"
export WAYPOINT_SUDO_PASSWORD="$PW"
export WAYPOINT_FTP_TEST="127.0.0.1:wpt:$PW:2122:2121:2990"
swift test --package-path "$MAC_DIR/WaypointCore"
