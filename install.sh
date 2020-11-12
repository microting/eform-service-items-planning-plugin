#!/bin/bash

if [ ! -d "/var/www/microting/eform-service-items-planning-plugin" ]; then
  cd /var/www/microting
  su ubuntu -c \
  "git clone https://github.com/microting/eform-service-items-planning-plugin.git -b stable"
fi

cd /var/www/microting/eform-service-items-planning-plugin
git pull
su ubuntu -c \
"dotnet restore ServiceItemsPlanningPlugin.sln"

echo "################## START GITVERSION ##################"
export GITVERSION=`git describe --abbrev=0 --tags | cut -d "v" -f 2`
echo $GITVERSION
echo "################## END GITVERSION ##################"
su ubuntu -c \
"dotnet publish ServiceItemsPlanningPlugin.sln -o out /p:Version=$GITVERSION --runtime linux-x64 --configuration Release"

su ubuntu -c \
"mkdir -p /var/www/microting/eform-debian-service/MicrotingService/out/Plugins/"

rm -fR /var/www/microting/eform-debian-service/MicrotingService/out/Plugins/ServiceItemsPlanningPlugin

su ubuntu -c \
"cp -av /var/www/microting/eform-service-items-planning-plugin/out /var/www/microting/eform-debian-service/MicrotingService/out/Plugins/ServiceItemsPlanningPlugin"
/root/rabbitmqadmin declare queue name=eform-service-items-planning-plugin durable=true
