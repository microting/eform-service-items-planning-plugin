#!/bin/bash

cd ~

if [ -d "Documents/workspace/microting/eform-debian-service/Plugins/ServiceItemsPlanningPlugin" ]; then
	rm -fR Documents/workspace/microting/eform-debian-service/Plugins/ServiceItemsPlanningPlugin
fi

mkdir Documents/workspace/microting/eform-debian-service/Plugins

cp -av Documents/workspace/microting/eform-service-items-planning-plugin/ServiceItemsPlanningPlugin Documents/workspace/microting/eform-debian-service/Plugins/ServiceItemsPlanningPlugin
