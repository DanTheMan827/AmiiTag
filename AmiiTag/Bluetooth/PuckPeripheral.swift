//
//  PuckPeripheral.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 8/2/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import SwiftyBluetooth
import CoreBluetooth

class PuckError: LocalizedError {
    var errorDescription: String? { return _description }
    var failureReason: String? { return _description }
    
    private var _description: String
    
    init(description: String) {
            self._description = description
        }
}

class PuckPeripheral: NSObject {
    fileprivate static let serviceUuid = "78290001-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static let commandUuid = "78290002-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static let responseUuid = "78290003-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static var observersRegistered = false
    static var pucks: [PuckPeripheral] = []
    static var scanning = false
    
    fileprivate let peripheral: Peripheral
    
    var name: String? {
        return peripheral.name
    }
    
    init(peripheral: Peripheral){
        self.peripheral = peripheral
    }
    
    func disconnect(completionHandler: @escaping (Result<Void, Error>) -> Void) {
        peripheral.readValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid) { (result) in
            self.peripheral.disconnect(completion: completionHandler)
        }
    }
    
    func changeName(name: String? = nil, completionHandler: @escaping (Result<Void, Error>) -> Void){
        var command = Data([0xFD] + (name ?? "").data(using: .utf8)!)
        
        peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
            self.disconnect { (result) in
                completionHandler(result)
            }
        }
    }
    
    func enableUart(completionHandler: @escaping (Result<Void, Error>) -> Void){
        var command = Data([0xFE])
        
        peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
            self.disconnect(completionHandler: completionHandler)
        }
    }
    
    func getSlotInformation(completionHandler: @escaping (Result<(current: UInt8, total: UInt8), Error>) -> Void){
        peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: Data([0x01]), type: .withResponse) { (result) in
            switch result {
            case .success(()):
                self.peripheral.readValue(ofCharacWithUUID: PuckPeripheral.responseUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid) { (result) in
                    switch result {
                    case .success(let response):
                        completionHandler(.success((current: response[1], total: response[2])))
                        break
                    case .failure(let error):
                        completionHandler(.failure(error))
                        break
                    }
                }
                break
            case .failure(let error):
                completionHandler(.failure(error))
                break
            }
        }
    }
    
    func changeSlot(slot: UInt8? = nil, completionHandler: @escaping (Result<Void, Error>) -> Void){
        var command: Data!
        if let slot = slot {
            command = Data([0xFF, slot])
        } else {
            command = Data([0xFF])
        }
        
        peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
            completionHandler(result)
        }
    }
    
    func readTag(slot: UInt8 = 255, completionHandler: @escaping (Result<Data, Error>) -> Void){
        _readTag(slot: slot, completionHandler: completionHandler)
    }
    
    fileprivate func _readTag(slot: UInt8, startPage: UInt8 = 0, count: UInt8 = 63, accumulatedData: Data? = nil, completionHandler: @escaping (Result<Data, Error>) -> Void){
        let command = Data([0x02, slot, startPage, count])
        print("Reading from \(peripheral.name ?? "puck") in slot \(slot) at page \(startPage) for \(count) pages")
        self.peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command) { (result) in
            switch result {
            case .success():
                self.peripheral.readValue(ofCharacWithUUID: PuckPeripheral.responseUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid) { (result) in
                    switch result {
                    case .success(let response):
                        let lastStart = response[2]
                        let lastCount = response[3]
                        let nextPage = lastStart + lastCount
                        let accumulatedData = Data((accumulatedData ?? Data(count: 0)) + response[4..<(4 + (Int(lastCount) * 4))])
                        
                        if startPage + count >= 143 {
                            completionHandler(.success(accumulatedData))
                        } else {
                            self._readTag(slot: slot, startPage: nextPage, count: min(UInt8(143) - startPage - count, count), accumulatedData: accumulatedData, completionHandler: completionHandler)
                        }
                        break
                    case .failure(let error):
                        completionHandler(.failure(error))
                        break
                    }
                }
                break
            case .failure(let error):
                completionHandler(.failure(error))
                break
            }
        }
    }
    
    func readAllTags(completionHandler: @escaping (Result<[Data], Error>) -> Void){
        getSlotInformation { (result) in
            switch result {
            case .success(let tagInformation):
                self._readAllTags(slot: 0, count: tagInformation.total, completionHandler: completionHandler)
                break
            case .failure(let error):
                completionHandler(.failure(error))
                break
            }
        }
    }
    
    fileprivate func _readAllTags(slot: UInt8 = 0, count: UInt8 = 5, tags: [Data] = [], completionHandler: @escaping (Result<[Data], Error>) -> Void){
        if slot < count {
            readTag(slot: slot) { (result) in
                switch result {
                case .success(let tag):
                    var tags: [Data] = tags
                    tags.append(Data(tag))
                    self._readAllTags(slot: slot + 1, count: count, tags: tags, completionHandler: completionHandler)
                    break
                case .failure(let error):
                    completionHandler(.failure(error))
                    break
                }
            }
        } else {
            completionHandler(.success(tags))
        }
    }
    
    func writeTag(toSlot slot: UInt8 = 255, using tag: Data, completionHandler: @escaping (Result<Void, Error>) -> Void){
        var data = Data(count: 572)
        data[0..<tag.count] = tag[0..<tag.count]
        _writeTag(toSlot: slot, withData: data, completionHandler: completionHandler)
    }
    
    fileprivate func _writeTag(toSlot slot: UInt8, atPage startPage: UInt8 = 0, withData data: Data, completionHandler: @escaping (Result<Void, Error>) -> Void){
        if startPage < 143 {
            let dataToWrite = Data(data[(Int(startPage) * 4)..<min(((Int(startPage) + 4) * 4), 572)])
            let command = Data([0x03, slot, startPage] + dataToWrite)
            print("Writing to \(peripheral.name ?? "puck") in slot \(slot) at page \(startPage) for \(dataToWrite.count) bytes")
            peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
                switch result {
                case .success(let _):
                    self._writeTag(toSlot: slot, atPage: startPage + 4, withData: data, completionHandler: completionHandler)
                    break
                case .failure(let error):
                    completionHandler(.failure(error))
                    break
                }
            }
        } else {
            completionHandler(.success(()))
        }
    }
    
    static func startScanning() {
        scanning = true
        
        SwiftyBluetooth.scanForPeripherals(withServiceUUIDs: [serviceUuid], timeoutAfter: 15) { scanResult in
            switch scanResult {
                case .scanStarted:
                    // The scan started meaning CBCentralManager scanForPeripherals(...) was called
                    pucks.removeAll()
                    break
                case .scanResult(let peripheral, let advertisementData, let RSSI):
                    // A peripheral was found, your closure may be called multiple time with a .ScanResult enum case.
                    // You can save that peripheral for future use, or call some of its functions directly in this closure.
                    pucks.append(PuckPeripheral(peripheral: peripheral))
                    break
                case .scanStopped(let error):
                    // The scan stopped, an error is passed if the scan stopped unexpectedly
                    if error.error == nil && scanning {
                        startScanning()
                    } else {
                        scanning = false
                        pucks.removeAll()
                    }
                    break
            }
        }
    }
    
    static func stopScanning() {
        scanning = false
        if SwiftyBluetooth.isScanning {
            SwiftyBluetooth.stopScan()
        }
    }
    
    static func registerObservers(){
        if !PuckPeripheral.observersRegistered {
            PuckPeripheral.observersRegistered = true
            
            NotificationCenter.default.addObserver(forName: Central.CentralStateChange, object: Central.sharedInstance, queue: nil) { (notification) in
                if let state = notification.userInfo?["state"] as? CBManagerState {
                    switch state {
                        case .poweredOn:
                            startScanning()
                            break
                            
                        case .poweredOff:
                            PuckPeripheral.pucks.removeAll()
                            break
                        default: break
                    }
                }
            }
        }
    }
}
