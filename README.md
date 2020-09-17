# OpenHistorian-OPC-UA-Adapter
Adapter which ingests OPC UA data into OpenHistorian.

How to configure:
1. Compile source code - make sure you use GSF dlls that correspond to your version of OpenHistorian
2. Create a folder within OpenHistorian folder called OPCUA
3. Copy all files from bin directory into OPCUA folder
4. Open the historian database using whichever SQL server you have chosen
5. Add new entry to Protocol table
    Acronym: OPCUA
    Name: OPCUA Adapted
    Type: Measurement
    Category: Device
    AssemblyName: OPCUA\OPCUAAdapter.dll
    TypeName: OPCUAAdapter.OPCUAAdapter
6. Create a new device (you may need to restart OpenHistorian)
    Protocol: select OPC UA Adapter from the list
    Connection String: opc.tcp://server_name:port (this connection string depends on your OPC UA server
    Make sure to fill Historian field and other compulsory fields
    Check Enabled
    Save
7. Add a new measurement
    Point Tag: meaningful name
    Signal Reference: NS=2;FOLDER.SUBFOLDER.OPCUATAGNAME
    Device: Select device you have created in Step 6
    Measurement Type: Analog (if the tag is a real)
    Fill in Description and Historian
    Check Enabled
8. Restart OpenHistorian
9. Examine the log file to see if there are any errors.
