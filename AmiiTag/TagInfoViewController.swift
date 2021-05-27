//
//  TagInfoViewController.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/28/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import CryptoKit
import CoreNFC

class TagInfoViewController: UIViewController, NFCTagReaderSessionDelegate {
    /*
    func finishedWriting(puck: PuckPeripheral) {
        self.dismiss(animated: true, completion: nil)
        puck.disconnect()
    }
    
    func puckDidReadTag(puck: PuckPeripheral, tag: TagDump) {
        
    }
    
    func puckReady(puck: PuckPeripheral) {
        puck.writeTag(tag: self.amiiboData!)
        
    }
    */
    
    @IBOutlet var amiiboArt: UIImageView!
    @IBOutlet var seriesName: UILabel!
    @IBOutlet var typeName: UILabel!
    @IBOutlet var tagUid: UILabel!
    
    var tagReaderSession: NFCTagReaderSession?
    var isLoaded = false
    
    fileprivate var _amiiboData: TagDump?
    var amiiboData: TagDump? {
        get {
            return _amiiboData
        }
        set(value) {
            _amiiboData = value
            if isLoaded {
                displayInfo(value: value)
            }
        }
    }
    var dump: Data {
        return amiiboData!.data
    }
    
    var fileName: String {
        let formatter = DateFormatter()
        formatter.timeZone = TimeZone.current
        formatter.dateFormat = "yyyy-MM-dd HH-mm-ss"
        
        return "\(self.title!) \(formatter.string(from: Date()))"
    }
    
    static func openTagInfo(dump: TagDump, controller: UIViewController){
        let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let view = storyboard.instantiateViewController(withIdentifier: "TagInfo") as? TagInfoViewController else {
            return
        }
        let nc = UINavigationController(rootViewController: view)
        controller.present(nc, animated: true)
        view.amiiboData = dump
    }
    
    func displayInfo(value: TagDump?) {
        guard let value = value else {
            return
        }
        
        print("Nickname: \(value.nickname)");
        print("Write Count: \(value.writeCounterInt)")
        
        tagUid.text = "0x\(value.uid.map { String(format: "%02hhx", $0) }.joined())"
        typeName.text = "0x\(value.headHex.prefix(8).suffix(2))"
        seriesName.text = "0x\(value.headHex.prefix(3))"
        
        amiiboArt.image = value.image
        
        self.title = value.displayName
        
        if let type = amiiboData?.typeName {
            typeName.text = type
        }
        
        if let series = value.amiiboSeriesName {
            seriesName.text = series
        }
        
    }
    
    @IBAction func saveTagTap(_ sender: Any) {
        let contentURL = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent(fileName)
            .appendingPathExtension("bin")
        
        guard let _ = try? dump.write(to: contentURL) else {
            return
        }

        // set up activity view controller
        let imageToShare = [ contentURL ]
        let activityViewController = UIActivityViewController(activityItems: imageToShare, applicationActivities: nil)
        activityViewController.popoverPresentationController?.sourceView = self.view // so that iPads won't crash
        activityViewController.completionWithItemsHandler = { (activityType, completed:Bool, returnedItems:[Any]?, error: Error?) in
           try? FileManager.default.removeItem(at: contentURL)
        }

        // present the view controller
        self.present(activityViewController, animated: true)
    }
    
    @IBAction func writeTagTap(_ sender: Any) {
        if (PuckPeripheral.pucks.count > 0) {
            let alertController = UIAlertController(title: nil, message: nil, preferredStyle: .actionSheet).Popify(view: self.view)
            alertController.view.tintColor = self.view.tintColor
            
            for puck in PuckPeripheral.pucks.sorted(by: { (a, b) -> Bool in
                return a.name > b.name
            }) {
                alertController.addAction(UIAlertAction(title: puck.name, style: .default, handler: { (action) in
                    let alert = UIAlertController(title: "Please Wait", message: "Writing " + (puck.name), preferredStyle: .alert)
                    self.present(alert, animated: true)
                    puck.writeTag(using: self.dump) { (result) in
                        var hasError = false
                        
                        switch result {
                        case .status(let status):
                            alert.message = "Writing \(puck.name) (\(status.start)/\(status.total))"
                        case .success(_):
                            break
                        case .failure(let error):
                            hasError = true
                            self.dismiss(animated: true) {
                                self.present(error.getAlertController(), animated: true)
                            }
                            break
                        }
                        
                        switch result {
                        case .status(_):
                            break
                        default:
                            puck.changeSlot { (result) in
                                switch result {
                                case .success(_):
                                    break;
                                case .failure(let error):
                                    hasError = true
                                    self.dismiss(animated: true) {
                                        self.present(error.getAlertController(), animated: true)
                                    }
                                    break
                                }
                                
                                puck.disconnect { (result) in
                                    if !hasError {
                                        self.dismiss(animated: true)
                                    }
                                    PuckPeripheral.startScanning()
                                }
                            }
                        }
                    }
                }))
            }
            
            alertController.addAction(UIAlertAction(title: "NFC", style: .default){ action -> Void in
                self.tagReaderSession = NFCTagReaderSession(pollingOption: NFCTagReaderSession.PollingOption.iso14443, delegate: self)
                
                self.tagReaderSession?.alertMessage = "Hold tag to phone"
                self.tagReaderSession?.begin()
            })
            
            alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel){ action -> Void in })
            self.present(alertController, animated:true){}
        } else {
            self.tagReaderSession = NFCTagReaderSession(pollingOption: NFCTagReaderSession.PollingOption.iso14443, delegate: self)
            
            self.tagReaderSession?.alertMessage = "Hold tag to phone"
            self.tagReaderSession?.begin()
        }
    }
    
    @IBAction func showQrTap(_ sender: Any) {
       let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let view = storyboard.instantiateViewController(withIdentifier: "QrView") as? QrViewController else {
            return
        }
        view.qrData = NSData(data: dump).base64EncodedString(options: .endLineWithLineFeed)
        view.fileName = self.fileName
        view.title = "\(self.title!) QR Code"
        self.present(UINavigationController(rootViewController: view), animated: true)
    }
    
    func writeDump(to ntag215Tag: NTAG215Tag, appData: Bool = false) {
        let dump = ntag215Tag.dump
        
        if appData {
            if "\(dump.headHex)\(dump.tailHex)" == "\(amiiboData!.headHex)\(amiiboData!.tailHex)" {
                if dump.data[0..<9].elementsEqual(amiiboData!.data[0..<9]) {
                    // Same tag, we don't need to re-encrypt anything
                    
                    ntag215Tag.writeAppData(dump) { (result) in
                        switch result {
                        case .success:
                            self.tagReaderSession?.invalidate()
                            
                            let alert = UIAlertController(title: "Tag app data successfully written", message: nil, preferredStyle: .alert)
                            alert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                            DispatchQueue.main.async {
                                self.present(alert, animated: true)
                            }
                            
                        case .failure(let error):
                            self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
                        }
                    }
                } else {
                    // Not the same tag, we need to re-encrypt
                    if !KeyFiles.hasKeys {
                        self.tagReaderSession?.invalidate(errorMessage: "Tag does not match the dump")
                        return
                    }
                    
                    ntag215Tag.patchAndWriteAppData(amiiboData!, staticKey: KeyFiles.staticKey!, dataKey: KeyFiles.dataKey!) {result in
                        switch result {
                        case .success:
                            self.tagReaderSession?.invalidate()
                            
                            let alert = UIAlertController(title: "Tag app data successfully written", message: nil, preferredStyle: .alert)
                            alert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                            DispatchQueue.main.async {
                                self.present(alert, animated: true)
                            }
                            
                        case .failure(let error):
                            self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
                        }
                    }
                }
            } else {
                self.tagReaderSession?.invalidate(errorMessage: "Tag character doesn't match")
            }
        } else {
            if !KeyFiles.hasKeys {
                self.tagReaderSession?.invalidate(errorMessage: "No keys loaded")
                return
            }
            
            ntag215Tag.patchAndWriteDump(amiiboData!, staticKey: KeyFiles.staticKey!, dataKey: KeyFiles.dataKey!) {result in
                switch result {
                case .success:
                    self.tagReaderSession?.invalidate()
                    
                    let alert = UIAlertController(title: "Tag successfully written", message: nil, preferredStyle: .alert)
                    alert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                    DispatchQueue.main.async {
                        self.present(alert, animated: true)
                    }
                    
                case .failure(let error):
                    self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
                }
            }
        }
    }
    
    func handleConnectedTag(tag: NFCMiFareTag) {
        tag.checkPuck { result in
            switch result {
            case .success(_):
                print("Found a puck")
                var pages: [(page: Int, data: Data)] = []
                for page in 0...(572/4) {
                    pages.append((page: page, data: Data(self.dump[(page * 4)..<(min(((page+1) * 4), 572))])))
                }
                
                tag.write(batch: pages) { result in
                    switch result {
                    case .success:
                        tag.sendMiFareCommand(commandPacket: Data([0x88])) { (data, error) in
                            self.tagReaderSession?.invalidate()
                            
                        }
                    case .failure(let error):
                        self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
                    }
                }
                
                break
                
            case .failure(_):
                print("Not a puck")
                NTAG215Tag.initialize(tag: tag) { result in
                    switch result {
                    case .success(let ntag215Tag):
                        if ntag215Tag.dump.isLocked {
                            if ntag215Tag.dump.isAmiibo {
                                self.writeDump(to: ntag215Tag, appData: true)
                            } else {
                                self.tagReaderSession?.invalidate(errorMessage: "Tag is not an amiibo")
                            }
                        } else {
                            self.writeDump(to: ntag215Tag)
                        }
                    case .failure(let error):
                        self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
                    }
                }
                break
            }
        }
    }
    
    override func viewDidLoad() {
        super.viewDidLoad()
        isLoaded = true
        if let data = amiiboData {
            displayInfo(value: data)
        }
    }
    
    // MARK: NFCTagReaderSessionDelegate
    func tagReaderSessionDidBecomeActive(_ session: NFCTagReaderSession) {
        NSLog("tagReaderSessionDidBecomeActive")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didInvalidateWithError error: Error) {
        NSLog("NFCTagReaderSession, didInvalidateWithError \(error)")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didDetect tags: [NFCTag]) {
        guard case let NFCTag.miFare(tag) = tags.first! else {
            tagReaderSession?.invalidate(errorMessage: "Invalid tag type")
            return
        }
        
        session.connect(to: tags.first!) { (error: Error?) in
            if let error = error {
                session.invalidate(errorMessage: error.localizedDescription)
            } else {
                self.handleConnectedTag(tag: tag)
            }
        }
    }
}
