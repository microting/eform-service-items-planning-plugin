#!/bin/bash

cd ~

if [ -d "Documents/workspace/microting/eform-debian-service/Plugins/ServiceItemsPlanningPlugin" ]; then
	rm -fR Documents/workspace/microting/eform-debian-service/Plugins/ServiceItemsPlanningPlugin
fi

cp -av Documents/workspace/microting/eform-service-itemsplanning-plugin/ServiceItemsPlanningPlugin Documents/workspace/microting/eform-debian-service/Plugins/ServiceItemsPlanningPlugin
