#!/bin/bash

cd ~

if [ -d "Documents/workspace/microting/eform-service-itemsplanning-plugin/ServiceItemsPlanningPlugin" ]; then
	rm -fR Documents/workspace/microting/eform-service-itemsplanning-plugin/ServiceItemsPlanningPlugin
fi

cp -av Documents/workspace/microting/eform-debian-service/Plugins/ServiceItemsPlanningPlugin Documents/workspace/microting/eform-service-itemsplanning-plugin/ServiceItemsPlanningPlugin
